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
        if (mode == "echo")
        {
            // Stdin to stdout, byte for byte, in small steps: both pipes fill while the other side works.
            using (var input = Console.OpenStandardInput())
            using (var output = Console.OpenStandardOutput())
            {
                var buffer = new byte[4096];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
            }
            return;
        }
        if (mode == "answer")
        {
            string line;
            var reader = new System.IO.StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            while ((line = reader.ReadLine()) != null) { Console.Write("got:" + line + "\n"); Console.Out.Flush(); }
            return;
        }
        if (mode == "endless")
        {
            using (var output = Console.OpenStandardOutput())
            {
                var chunk = new byte[8192];
                while (true) output.Write(chunk, 0, chunk.Length);
            }
        }
        if (mode == "stderr-flood")
        {
            using (var error = Console.OpenStandardError())
            {
                var line = Encoding.ASCII.GetBytes(new string('e', 63) + "\n");
                while (true) error.Write(line, 0, line.Length);
            }
        }
        if (mode == "fail-early") { Console.Error.Write("refused"); Environment.Exit(3); }
        if (mode == "slow")
        {
            Console.WriteLine(Process.GetCurrentProcess().Id);
            Console.Out.Flush();
            Thread.Sleep(int.Parse(args[2]));
            Console.Write("done");
            return;
        }
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
