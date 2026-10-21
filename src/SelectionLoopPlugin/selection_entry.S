/* Windows x64 internal-site ABI; exact original MovingRenders 4.21 only. */
.text
.p2align 4
.globl SelectionLoopEntry
.def SelectionLoopEntry; .scl 2; .type 32; .endef
SelectionLoopEntry:
    pushq %rdx                       /* original segment byte offset */
    pushq %r14                       /* original wrestler ID, before replay */
    /* Exact 25 original bytes at RVA1A42, preserving original CMP flags. */
    .byte 0x42,0x8b,0x5c,0x02,0x04,0x46,0x8b,0x74,0x02,0x08
    .byte 0x42,0x83,0x7c,0x02,0x0c,0x00,0x41,0x0f,0x95,0xc4
    .byte 0xba,0x01,0x00,0x00,0x00
    pushfq
    pushq %rax
    pushq %rcx
    pushq %rdx
    pushq %rbx
    pushq %rbp
    pushq %rsi
    pushq %rdi
    pushq %r8
    pushq %r9
    pushq %r10
    pushq %r11
    pushq %r12
    pushq %r13
    pushq %r14
    pushq %r15
    subq $144, %rsp                  /* aligned; 32-byte shadow + XMM saves */
    movdqu %xmm0, 40(%rsp)
    movdqu %xmm1, 56(%rsp)
    movdqu %xmm2, 72(%rsp)
    movdqu %xmm3, 88(%rsp)
    movdqu %xmm4, 104(%rsp)
    movdqu %xmm5, 120(%rsp)
    movl 272(%rsp), %ecx             /* original wrestler ID */
    movl 232(%rsp), %edx             /* original start */
    movl 152(%rsp), %r8d             /* original endExclusive */
    movzbl 168(%rsp), %r9d           /* original bool freeze */
    movq 200(%rsp), %rax             /* original R8 segment table base */
    addq 280(%rsp), %rax             /* original RDX segment byte offset */
    movq %rax, 32(%rsp)              /* fifth Windows argument */
    call SelectionLoopOverride
    movl %eax, 152(%rsp)             /* saved R14 low32; upper stays zero */
    shrq $32, %rax
    movb %al, 168(%rsp)              /* saved R12 low8, preserves upper56 */
    movdqu 40(%rsp), %xmm0
    movdqu 56(%rsp), %xmm1
    movdqu 72(%rsp), %xmm2
    movdqu 88(%rsp), %xmm3
    movdqu 104(%rsp), %xmm4
    movdqu 120(%rsp), %xmm5
    addq $144, %rsp
    popq %r15
    popq %r14
    popq %r13
    popq %r12
    popq %r11
    popq %r10
    popq %r9
    popq %r8
    popq %rdi
    popq %rsi
    popq %rbp
    popq %rbx
    popq %rdx
    popq %rcx
    popq %rax
    popfq
    leaq 16(%rsp), %rsp               /* remove ID + offset without changing FLAGS */
    jmp *g_selection_resume(%rip)
