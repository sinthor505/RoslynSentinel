using System.Text.RegularExpressions;

using ModelContextProtocol;

namespace RoslynSentinel.Common;

internal class EngineProgressParser
{
    static ProgressNotificationValue ParseEngineProgress(string msg)
    {
        var m = Regex.Match(msg, @"^(\d+) of (\d+)\.");
        if (m.Success)
            return new ProgressNotificationValue
            {
                Progress = float.Parse(m.Groups[1].Value),
                Total = float.Parse(m.Groups[2].Value),
                Message = msg
            };
        return new ProgressNotificationValue { Progress = 0, Message = msg };
    }
}
