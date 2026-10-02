using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;

// Compiled into a temporary executable by ProcessRunnerTests; never launched in the real project.
internal static class ProcessFixture
{
    private static void Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string mode = args[0];
        if (mode == "text") { Console.Write("héllo\nlast"); Console.Error.Write("érr\n"); return; }
        if (mode == "newlines") { Console.Write("one\rtwo\r\nthree\n\nlast"); return; }
        if (mode == "bom") { Console.Write("\ufeffhéllo"); return; }
        if (mode == "flood")
        {
            for (int i = 0; i < 128; i++) { Console.Write(new string('o', 8192)); Console.Error.Write(new string('e', 8192)); }
            return;
        }
        if (mode == "binary" || mode == "split-utf8")
        {
            byte[] bytes = mode == "binary" ? new byte[256] : Encoding.UTF8.GetBytes("hé🦊\nlast");
            if (mode == "binary") for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
            using (var output = Console.OpenStandardOutput())
                foreach (byte value in bytes) { output.WriteByte(value); output.Flush(); if (mode != "binary") Thread.Sleep(10); }
            return;
        }
        if (mode == "stdin") { Console.Write(Console.In.ReadToEnd().Length); return; }
        if (mode == "child-pipe" || mode == "child-sleep")
        {
            var child = Process.Start(new ProcessStartInfo(args[1], "\"" + Assembly.GetExecutingAssembly().Location + "\" sleep")
            { UseShellExecute = false, CreateNoWindow = true });
            Console.WriteLine("CHILD:" + child.Id);
            Console.Out.Flush();
            Thread.Sleep(200);
            if (mode == "child-pipe") return;
        }
        Console.WriteLine(Process.GetCurrentProcess().Id);
        Console.Out.Flush();
        Thread.Sleep(30000);
    }
}
