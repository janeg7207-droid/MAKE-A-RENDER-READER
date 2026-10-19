using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static string root;
    private static async Task<int> Main()
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-batch-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string output = Directory.CreateDirectory(Path.Combine(root, "Configured output with spaces")).FullName;
            string existingMap = Path.Combine(output, "_roster_pictures.txt");
            File.WriteAllText(existingMap, "Keep the existing normal-output roster map");
            var first = Job("first", 924, "PAC");
            byte[] source = File.ReadAllBytes(first.RenderPath);
            string sourcePath = first.RenderPath, workspace = first.OutputDirectory;
            string destination = BatchRenderPlan.PublishCompleted(first, output);
            Check(destination == Path.Combine(output, "924_PAC.gmr"), "A free native filename is saved directly in the configured normal output folder");
            Check(first.PublishedRenderPath == destination, "The job records its visible published path");
            Check(File.ReadAllBytes(destination).SequenceEqual(source), "The visible completed render is byte-identical to the verified workspace source");
            Check(first.RenderPath == sourcePath && first.OutputDirectory == workspace, "The installer retains its original verified workspace and source path");
            Check(File.ReadAllBytes(sourcePath).SequenceEqual(source), "Publishing leaves the completed workspace movie unchanged");
            Check(File.ReadAllText(Path.Combine(workspace, "_roster_pictures.txt")) == "924 FFFFFFFFFFFFFFFF\n", "Publishing keeps the per-job roster picture map available for installation");
            Check(File.ReadAllText(existingMap) == "Keep the existing normal-output roster map", "A different existing normal-output map is never replaced by a batch export");
            Check(BatchRenderPlan.PublishCompleted(first, output) == destination && Directory.GetFiles(output, "*.gmr").Length == 1, "Retrying a completed publication reuses its identical copy without creating duplicates");

            var second = Job("second", 924, "PAC");
            string secondPath = BatchRenderPlan.PublishCompleted(second, output);
            Check(secondPath != destination && Path.GetDirectoryName(secondPath) == output, "A colliding render is given a unique filename in the same ordinary output folder");
            Check(Path.GetFileName(secondPath).StartsWith("924_PAC_batch-", StringComparison.Ordinal) && secondPath.EndsWith(".gmr", StringComparison.Ordinal), "Unique filenames retain the logical wrestler ID prefix and GMR extension");
            Check(File.ReadAllBytes(destination).SequenceEqual(source), "An existing normal render is never overwritten on collision");
            string directoryOutput = Directory.CreateDirectory(Path.Combine(root, "directory collision")).FullName;
            Directory.CreateDirectory(Path.Combine(directoryOutput, "924_PAC.gmr"));
            Check(File.Exists(BatchRenderPlan.PublishCompleted(Job("directory", 924, "PAC"), directoryOutput)), "A directory occupying the original filename is preserved and a unique file is used");

            File.WriteAllText(secondPath, "An externally changed existing render must survive");
            string replacement = BatchRenderPlan.PublishCompleted(second, output);
            Check(replacement != secondPath && File.ReadAllText(secondPath) == "An externally changed existing render must survive", "An altered previous export is preserved instead of being overwritten by a retry");
            Check(File.ReadAllBytes(replacement).SequenceEqual(source), "Retrying an altered export writes a fresh verified copy");

            var cancelled = Job("cancelled", 122, "Avery");
            string cancelOutput = Path.Combine(root, "cancelled output");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Throws<OperationCanceledException>(() => BatchRenderPlan.PublishCompleted(cancelled, cancelOutput, cancellation.Token), "A cancelled publication stops before creating output files");
            }
            Check(!Directory.Exists(cancelOutput) && cancelled.PublishedRenderPath == null, "Cancellation leaves no partially published result");
            Check(File.Exists(cancelled.RenderPath) && File.Exists(Path.Combine(cancelled.OutputDirectory, "_roster_pictures.txt")), "Cancellation preserves the finished render and its installation context");
            Check(File.ReadAllBytes(destination).SequenceEqual(source), "Cancelling a later job leaves earlier completed output intact");

            var failed = Job("failed", 4000, "Jon_Moxley");
            string blockedOutput = Path.Combine(root, "blocked output");
            File.WriteAllText(blockedOutput, "Existing file at the requested output location");
            Throws<IOException>(() => BatchRenderPlan.PublishCompleted(failed, blockedOutput), "An unavailable output directory is reported as a copy failure");
            Check(failed.PublishedRenderPath == null && failed.Status == "Complete", "An output failure does not turn the completed workspace render into an invalid source");
            Check(File.Exists(failed.RenderPath) && File.Exists(Path.Combine(failed.OutputDirectory, "_roster_pictures.txt")), "Failed publication keeps its movie and roster map for recovery or installation");
            Check(File.ReadAllText(blockedOutput) == "Existing file at the requested output location", "An output-location failure preserves the existing blocking file");
            Check(File.Exists(BatchRenderPlan.PublishCompleted(failed, output)), "A finished render can be published again to a working destination after failure");

            var invalid = Job("invalid", 6000, "Wrong");
            invalid.Entry.Id = 6001;
            string invalidOutput = Path.Combine(root, "invalid output");
            Throws<InvalidDataException>(() => BatchRenderPlan.PublishCompleted(invalid, invalidOutput), "A mismatched logical wrestler ID cannot be exported as a completed job");
            Check(!Directory.Exists(invalidOutput) && File.Exists(invalid.RenderPath), "Validation failure creates no output and retains the source");
            var wrongName = Job("wrong filename", 924, "PAC");
            string originalName = wrongName.RenderPath;
            wrongName.RenderPath = Path.Combine(wrongName.OutputDirectory, "1173_wrong.gmr"); File.Move(originalName, wrongName.RenderPath);
            Throws<InvalidDataException>(() => BatchRenderPlan.PublishCompleted(wrongName, output), "A filename claiming a physical ID cannot be exported for a different logical slot");

            var parallel = Enumerable.Range(0, 6).Select(index => Job("parallel-" + index, 910, "Adam_Cole")).ToArray();
            string[] paths = await Task.WhenAll(parallel.Select(job => Task.Run(() => BatchRenderPlan.PublishCompleted(job, output))));
            Check(paths.Distinct().Count() == parallel.Length, "Concurrent exports racing for the same native filename all get distinct files");
            Check(paths.All(path => Path.GetDirectoryName(path) == output && File.Exists(path)), "All competing exports still arrive directly in the ordinary output folder");
            Check(paths.Zip(parallel, (path, job) => File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(job.RenderPath))).All(value => value), "Concurrent collision handling keeps every completed movie unchanged");
            Check(Directory.GetFiles(output, ".gorilla-batch-*").Length == 0, "Completed, failed and cancelled exports leave no staging files in the output folder");
            Check(File.ReadAllText(existingMap) == "Keep the existing normal-output roster map", "Repeated and competing exports continue to preserve the normal folder's map");
            if (Path.DirectorySeparatorChar == '/')
            {
                string realOutput = Directory.CreateDirectory(Path.Combine(root, "redirected output target")).FullName;
                string linkedOutput = Path.Combine(root, "configured output link");
                Directory.CreateSymbolicLink(linkedOutput, realOutput);
                string linkedResult = BatchRenderPlan.PublishCompleted(Job("redirected", 122, "Avery"), linkedOutput);
                Check(Path.GetDirectoryName(linkedResult) == linkedOutput, "A legitimate redirected configured output path is accepted");
                Check(File.Exists(Path.Combine(realOutput, Path.GetFileName(linkedResult))), "The redirected folder receives the completed render through its configured path");
            }
            Console.WriteLine(passed + " batch output publication checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static BatchRenderJob Job(string workspace, int id, string name)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "workspaces", workspace)).FullName;
        string path = Path.Combine(directory, id + "_" + name + ".gmr");
        using (var stream = File.Create(path)) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("BIKi"));
            foreach (uint value in new uint[] { 56, 2, 4, 2, 1024, 1024, 30, 1, 1048576, 0, 57, 61, 64 }) writer.Write(value);
            writer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }); // Header fixture, not a decoded movie.
            byte[] metadata = Encoding.UTF8.GetBytes("{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":" + id + "}");
            writer.Write(metadata); writer.Write((uint)metadata.Length); writer.Write(Encoding.ASCII.GetBytes("GMR1"));
        }
        File.WriteAllText(Path.Combine(directory, "_roster_pictures.txt"), id + " FFFFFFFFFFFFFFFF\n");
        return new BatchRenderJob { Entry = new RosterEntry { Id = id, Name = name }, RenderPath = path, OutputDirectory = directory, Status = "Complete" };
    }
    private static void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); passed++; }
    private static void Throws<T>(Action action, string description) where T : Exception
    {
        try { action(); } catch (T) { passed++; return; }
        throw new InvalidOperationException(description);
    }
}
