using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;

// TODO: Turn these into libs and run them in succession from C# executable which imports both C# lib and F# lib

RunExample(Example1_1.Example.Run, "Example #1.1: Referential Transparency");
RunExample(Example1_2.Example.Run, "Example #1.2: Purity");
RunExample(Example1_3.Example.Run, "Example #1.3: Immutability");
RunExample(Example3_1.Example.Run, "Example #3.1: Composability");
RunExample(Example3_2.Example.Run, "Example #3.2: Concurrency & Parallelism Safety");
RunExample(Example3_3.Example.Run, "Example #3.3: Reasoning, Debugging, & Refactoring");
RunExample(Example3_4.Example.Run, "Example #3.4: Testing");


static void RunExample(Action action, String label)
{
    try
    {
        Console.WriteLine($"\n\n{new string('-', label.Length + 6)}");
        Console.WriteLine($" {label} (C#)");
        Console.WriteLine($"{new string('-', label.Length + 6)}\n");
        action();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FOUND ERROR: {ex.Message}");
    }
}