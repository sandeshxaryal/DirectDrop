namespace DirectDrop.Core.Models;

public static class DeviceNameParser
{
    public static string GetFriendlyName(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "Connected phone";

        string ua = userAgent;

        if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase))
            return "iPad";

        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
            return "iPhone";

        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            // Common Android UA shape:
            // Android 14; SM-S918B Build/...; ...
            int android = ua.IndexOf("Android", StringComparison.OrdinalIgnoreCase);
            int semi = ua.IndexOf(';', android);
            if (semi >= 0)
            {
                int nextSemi = ua.IndexOf(';', semi + 1);
                string model = nextSemi >= 0
                    ? ua.Substring(semi + 1, nextSemi - semi - 1).Trim()
                    : ua.Substring(semi + 1).Trim();

                int build = model.IndexOf(" Build/", StringComparison.OrdinalIgnoreCase);
                if (build >= 0) model = model[..build].Trim();

                if (!string.IsNullOrWhiteSpace(model) &&
                    !model.Equals("wv", StringComparison.OrdinalIgnoreCase))
                    return $"Android · {model}";
            }

            return "Android phone";
        }

        if (ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase))
            return "Mac";

        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            return "Windows device";

        return "Connected device";
    }
}
