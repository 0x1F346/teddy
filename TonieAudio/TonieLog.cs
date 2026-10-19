using System;

namespace TonieFile
{
    /* Minimal log hook so library messages are visible in GUI hosts too.
       Defaults to Console for the CLI; TeddyBench redirects it to its log window. */
    public static class TonieLog
    {
        public static Action<string> Handler = msg => Console.WriteLine(msg);

        public static void Log(string message)
        {
            Handler?.Invoke(message);
        }
    }
}
