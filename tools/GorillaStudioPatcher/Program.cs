using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

internal static class Program
{
    private const string BridgeType = "GorillaStudio.NameFolders.FolderBridge";
    private const string MainWindowType = "Gorilla.RenderStudio.MainWindow";

    public static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: GorillaStudioPatcher original.exe GorillaStudio.NameFolders.dll output.exe");
            return 2;
        }

        string original = Path.GetFullPath(args[0]);
        string helperPath = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[2]);
        bool wroteOutput = false;
        try
        {
            if (!File.Exists(original) || !File.Exists(helperPath))
                throw new FileNotFoundException("The original executable and the built helper DLL must exist.");
            if (string.Equals(original, output, StringComparison.OrdinalIgnoreCase) || File.Exists(output))
                throw new InvalidOperationException("Output must be a new file; the original executable is never overwritten.");

            using var module = ModuleDefMD.Load(original);
            using var helper = ModuleDefMD.Load(helperPath);
            if (module.IsStrongNameSigned)
                throw new InvalidOperationException("A signed executable requires its publisher's signing key; this patcher will not remove the signature.");
            MethodDef bridge = helper.GetTypes()
                .Single(t => t.FullName == BridgeType).Methods
                .Single(m => m.Name == "PrepareFolder" && m.IsPublic && m.IsStatic &&
                    m.MethodSig.Params.Count == 1 && m.MethodSig.Params[0].ElementType == ElementType.String &&
                    m.MethodSig.RetType.ElementType == ElementType.String);
            MethodDef initialize = helper.GetTypes()
                .Single(t => t.FullName == BridgeType).Methods
                .Single(m => m.Name == "Initialize" && m.IsPublic && m.IsStatic &&
                    m.MethodSig.Params.Count == 1 && m.MethodSig.Params[0].ElementType == ElementType.Object &&
                    m.MethodSig.RetType.ElementType == ElementType.Void);
            // Compiler-generated state-machine numbers change between release versions.
            // Follow the method's actual AsyncStateMachine attribute instead of a name.
            TypeDef stateMachine = FindFolderStateMachine(module);
            MethodDef method = stateMachine.Methods.Single(m => m.Name == "MoveNext");
            FieldDef folderField = stateMachine.Fields.Single(f => f.Name == "folder");
            FieldDef stateField = stateMachine.Fields.Single(f => f.Name == "<>1__state");
            FieldDef ownerField = stateMachine.Fields.Single(f => f.Name == "<>4__this");
            FieldDef busyField = ownerField.FieldType.ToTypeDefOrRef().ResolveTypeDefThrow().Fields.Single(f => f.Name == "Busy");
            if (method.Body.Instructions.Any(i => i.Operand is IMethod m && m.DeclaringType.FullName == BridgeType))
                throw new InvalidOperationException("This executable already calls the folder picker.");
            if (!method.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == @"^\d+_.+"))
                throw new InvalidOperationException("The expected folder-name recognition was not found; refusing an unknown executable layout.");

            // The first branch distinguishes the initial call from the resumed roster lookup.
            // Insert only on the initial path, so the modal picker is never opened on resume.
            IList<Instruction> instructions = method.Body.Instructions;
            if (instructions.Count < 8 || instructions[0].OpCode != OpCodes.Ldarg_0 ||
                instructions[1].OpCode != OpCodes.Ldfld || !ReferenceEquals(instructions[1].Operand, stateField) ||
                instructions[2].OpCode != OpCodes.Stloc_0 || instructions[3].OpCode != OpCodes.Ldarg_0 ||
                instructions[4].OpCode != OpCodes.Ldfld || !ReferenceEquals(instructions[4].Operand, ownerField) ||
                instructions[5].OpCode != OpCodes.Stloc_1 || instructions[6].OpCode != OpCodes.Ldloc_0 ||
                (instructions[7].OpCode != OpCodes.Brfalse && instructions[7].OpCode != OpCodes.Brfalse_S))
                throw new InvalidOperationException("The initial/resumed async folder paths do not match the supported releases.");
            int insertionIndex = 8;
            Instruction originalStart = instructions[insertionIndex];
            int lastStateStore = instructions.Select((instruction, index) => (instruction, index))
                .Last(pair => pair.instruction.OpCode == OpCodes.Stfld && ReferenceEquals(pair.instruction.Operand, stateField)).index;
            Instruction complete = instructions[lastStateStore - 2];
            if (complete.OpCode != OpCodes.Ldarg_0 || !instructions[lastStateStore - 1].IsLdcI4() ||
                instructions[lastStateStore - 1].GetLdcI4Value() != -2 ||
                !instructions.Skip(lastStateStore).Any(i => i.Operand is IMethod m && m.Name == "SetResult"))
                throw new InvalidOperationException("The async completion path does not match the expected executable.");
            if (!method.Body.ExceptionHandlers.Any(handler => handler.HandlerType == ExceptionHandlerType.Catch &&
                instructions.IndexOf(handler.TryStart) <= insertionIndex &&
                instructions.IndexOf(handler.TryEnd) > insertionIndex &&
                instructions.IndexOf(handler.TryEnd) < instructions.IndexOf(complete)))
                throw new InvalidOperationException("The picker must be inside the existing async exception handler and leave to its normal completion path.");

            IMethod bridgeReference = new Importer(module).Import(bridge);
            Instruction prepareStart = Instruction.Create(OpCodes.Ldarg_0);
            var injected = new[]
            {
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, ownerField),
                Instruction.Create(OpCodes.Ldfld, busyField),
                Instruction.Create(OpCodes.Brfalse, prepareStart),
                // Both exits cross the async try boundary, so they must use leave.
                Instruction.Create(OpCodes.Leave, complete),
                prepareStart,
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, folderField),
                Instruction.Create(OpCodes.Call, bridgeReference),
                Instruction.Create(OpCodes.Stfld, folderField),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, folderField),
                Instruction.Create(OpCodes.Brtrue, originalStart),
                Instruction.Create(OpCodes.Leave, complete)
            };
            for (int i = 0; i < injected.Length; i++)
                instructions.Insert(insertionIndex + i, injected[i]);

            // InitializeComponent has created and named the original controls at this point.
            // The helper adds one roster button at runtime without modifying WPF resources.
            TypeDef window = stateMachine.DeclaringType;
            MethodDef constructor = window.Methods.Single(candidate => candidate.IsInstanceConstructor && candidate.HasBody &&
                candidate.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Call &&
                    instruction.Operand is IMethod called && called.Name == "InitializeComponent" &&
                    called.DeclaringType.FullName == MainWindowType));
            Instruction initializeComponent = constructor.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Call &&
                instruction.Operand is IMethod called && called.Name == "InitializeComponent" &&
                called.DeclaringType.FullName == MainWindowType);
            int initializeIndex = constructor.Body.Instructions.IndexOf(initializeComponent) + 1;
            constructor.Body.Instructions.Insert(initializeIndex, Instruction.Create(OpCodes.Ldarg_0));
            constructor.Body.Instructions.Insert(initializeIndex + 1,
                Instruction.Create(OpCodes.Call, new Importer(module).Import(initialize)));

            // Keep every untouched body's bytecode, local signatures, and stack header unchanged.
            // The injected sequence requires only two stack slots; the original maximum is larger.
            foreach (MethodDef existing in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
                existing.Body.KeepOldMaxStack = true;
            var options = new ModuleWriterOptions(module);
            options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            wroteOutput = true;
            module.Write(output, options);

            VerifyUnchangedBodies(original, output, new HashSet<int> { method.MDToken.ToInt32(), constructor.MDToken.ToInt32() });
            VerifyManagedResources(module, output);
            VerifyNativeResources(original, output);
            VerifyInjectedCall(output);
            Console.WriteLine($"Created {output}");
            Console.WriteLine($"Folder state machine: {stateMachine.FullName}");
            Console.WriteLine("Verified: only SetFolder.MoveNext and MainWindow constructor IL changed; all other method bodies, licensing/authentication, managed resources, and Windows resources are preserved.");
            return 0;
        }
        catch (Exception exception)
        {
            if (wroteOutput) File.Delete(output);
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static TypeDef FindFolderStateMachine(ModuleDef module)
    {
        TypeDef window = module.GetTypes().Single(t => t.FullName == MainWindowType);
        MethodDef setFolder = window.Methods.Single(method => method.Name == "SetFolder" &&
            !method.IsStatic && method.MethodSig.Params.Count == 1 &&
            method.MethodSig.Params[0].ElementType == ElementType.String &&
            method.MethodSig.RetType.FullName == "System.Threading.Tasks.Task");
        var asyncAttribute = setFolder.CustomAttributes.Single(attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
        if (asyncAttribute.ConstructorArguments.Count != 1 ||
            asyncAttribute.ConstructorArguments[0].Value is not TypeSig type)
            throw new InvalidOperationException("The folder method has no readable async-state-machine declaration.");
        TypeDef stateMachine = type.ToTypeDefOrRef().ResolveTypeDefThrow();
        if (stateMachine.DeclaringType != window ||
            !stateMachine.Name.String.StartsWith("<SetFolder>d__", StringComparison.Ordinal))
            throw new InvalidOperationException("The folder method's async state machine is not nested in the expected main window.");
        return stateMachine;
    }

    private static void VerifyUnchangedBodies(string original, string output, ISet<int> changedTokens)
    {
        using var originalStream = File.OpenRead(original);
        using var outputStream = File.OpenRead(output);
        using var before = new PEReader(originalStream);
        using var after = new PEReader(outputStream);
        MetadataReader beforeMetadata = before.GetMetadataReader();
        MetadataReader afterMetadata = after.GetMetadataReader();
        int checkedMethods = 0;
        foreach (MethodDefinitionHandle handle in beforeMetadata.MethodDefinitions)
        {
            MethodDefinition oldMethod = beforeMetadata.GetMethodDefinition(handle);
            MethodDefinition newMethod = afterMetadata.GetMethodDefinition(handle);
            if (changedTokens.Contains(MetadataTokens.GetToken(handle))) continue;
            if (oldMethod.RelativeVirtualAddress == 0)
            {
                if (newMethod.RelativeVirtualAddress != 0) throw new InvalidDataException("A method without IL unexpectedly changed.");
                continue;
            }
            MethodBodyBlock oldBody = before.GetMethodBody(oldMethod.RelativeVirtualAddress);
            MethodBodyBlock newBody = after.GetMethodBody(newMethod.RelativeVirtualAddress);
            bool equal = oldBody.GetILBytes()!.SequenceEqual(newBody.GetILBytes()!) &&
                oldBody.MaxStack == newBody.MaxStack &&
                oldBody.LocalVariablesInitialized == newBody.LocalVariablesInitialized &&
                oldBody.LocalSignature == newBody.LocalSignature &&
                oldBody.ExceptionRegions.Select(ExceptionShape).SequenceEqual(newBody.ExceptionRegions.Select(ExceptionShape));
            if (!equal)
                throw new InvalidDataException($"Untouched method IL changed: {beforeMetadata.GetString(oldMethod.Name)} (0x{MetadataTokens.GetToken(handle):X8}).");
            checkedMethods++;
        }
        if (checkedMethods == 0) throw new InvalidDataException("No unchanged method bodies were verified.");
    }

    private static string ExceptionShape(ExceptionRegion region) =>
        $"{region.Kind}:{region.TryOffset}:{region.TryLength}:{region.HandlerOffset}:{region.HandlerLength}:{region.FilterOffset}:{MetadataTokens.GetToken(region.CatchType)}";

    private static void VerifyManagedResources(ModuleDef original, string output)
    {
        using var patched = ModuleDefMD.Load(output);
        static string Shape(Resource resource) => resource switch
        {
            EmbeddedResource embedded => $"{resource.Name}:{resource.Attributes}:" +
                Convert.ToHexString(SHA256.HashData(embedded.CreateReader().ToArray())),
            _ => $"{resource.Name}:{resource.Attributes}:{resource.ResourceType}:{resource}"
        };
        if (!original.Resources.Select(Shape).SequenceEqual(patched.Resources.Select(Shape)))
            throw new InvalidDataException("Managed resources changed, including the WPF user interface.");
    }

    private static void VerifyNativeResources(string original, string output)
    {
        if (!NativeResources(original).OrderBy(p => p.Key).SequenceEqual(NativeResources(output).OrderBy(p => p.Key)))
            throw new InvalidDataException("Windows native resource contents changed.");
    }

    // Resource table offsets are allowed to move when the PE is rebuilt; compare each
    // logical resource's path, code page, and bytes instead of its absolute RVA.
    private static Dictionary<string, string> NativeResources(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        DirectoryEntry directory = pe.PEHeaders.PEHeader!.ResourceTableDirectory;
        var result = new Dictionary<string, string>();
        if (directory.RelativeVirtualAddress == 0) return result;
        byte[] root = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent().ToArray();
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(offset, 2));
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(offset, 4));
        void Walk(int offset, string prefix, int depth)
        {
            if (depth > 8) throw new InvalidDataException("Invalid native resource nesting.");
            int count = U16(offset + 12) + U16(offset + 14);
            for (int index = 0; index < count; index++)
            {
                int entry = offset + 16 + index * 8;
                uint name = U32(entry);
                string component = (name & 0x80000000) == 0 ? $"id:{name}" :
                    "name:" + Encoding.Unicode.GetString(root, checked((int)(name & 0x7fffffff)) + 2,
                        U16(checked((int)(name & 0x7fffffff))) * 2);
                string resourcePath = prefix + "/" + component;
                uint child = U32(entry + 4);
                int childOffset = checked((int)(child & 0x7fffffff));
                if ((child & 0x80000000) != 0) Walk(childOffset, resourcePath, depth + 1);
                else
                {
                    int rva = checked((int)U32(childOffset));
                    int size = checked((int)U32(childOffset + 4));
                    uint codePage = U32(childOffset + 8);
                    byte[] bytes = pe.GetSectionData(rva).GetContent(0, size).ToArray();
                    result.Add(resourcePath, $"{codePage}:{Convert.ToHexString(SHA256.HashData(bytes))}");
                }
            }
        }
        Walk(0, "", 0);
        return result;
    }

    private static void VerifyInjectedCall(string output)
    {
        using var module = ModuleDefMD.Load(output);
        MethodDef moveNext = FindFolderStateMachine(module).Methods.Single(m => m.Name == "MoveNext");
        int calls = moveNext.Body.Instructions.Count(i => i.OpCode == OpCodes.Call && i.Operand is IMethod method &&
            method.DeclaringType.FullName == BridgeType && method.Name == "PrepareFolder");
        if (calls != 1) throw new InvalidDataException("The saved executable does not contain exactly one folder-picker call.");
        int initializeCalls = module.GetTypes().Single(t => t.FullName == MainWindowType).Methods
            .Where(method => method.IsInstanceConstructor && method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Count(instruction => instruction.OpCode == OpCodes.Call && instruction.Operand is IMethod called &&
                called.DeclaringType.FullName == BridgeType && called.Name == "Initialize");
        if (initializeCalls != 1) throw new InvalidDataException("The saved executable does not contain exactly one roster-button initialization call.");
    }
}
