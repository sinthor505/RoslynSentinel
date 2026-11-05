public static class Program
{
    public static int Main()
    {
        try
        {
            using Stream stdin = Console.OpenStandardInput();
            using MemoryStream buffer = new();
            stdin.CopyTo(buffer);
            byte[]? output = RoslynSentinel.Utilities.AgentIdentityHook.AgentIdentityHookTransform.Transform(buffer.ToArray());
            if (output is not null)
            {
                using Stream stdout = Console.OpenStandardOutput();
                stdout.Write(output, 0, output.Length);
                stdout.Flush();
            }
        }
        catch (Exception)
        {
            // Fail open deliberately: this runs on every tool call, so a hook problem must never block one.
        }

        return 0;
    }
}
