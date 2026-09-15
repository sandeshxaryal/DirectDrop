using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace DirectDrop.App.Services;

public static class QrCodeService
{
    /// <summary>
    /// Renders <paramref name="content"/> (the DirectDrop connection URL,
    /// including its token) as a QR code PNG and loads it as a
    /// BitmapImage ready to bind to an Image control.
    /// ECCLevel.Q (25% error-correction) is used because phone cameras at
    /// an angle, or a slightly glossy monitor, are the realistic conditions
    /// this code will actually be scanned under.
    /// </summary>
    public static BitmapImage GeneratePng(string content, int pixelsPerModule = 12)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var pngQr = new PngByteQRCode(data);
        byte[] pngBytes = pngQr.GetGraphic(pixelsPerModule);

        var image = new BitmapImage();
        using var stream = new MemoryStream(pngBytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // load fully now, so the MemoryStream can be disposed
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze(); // makes it safe to hand to the UI thread from a background thread
        return image;
    }

    /// <summary>
    /// Builds the payload for a "join this Wi-Fi network" QR code, per the
    /// de-facto WIFI: URI format the iOS and Android camera apps both
    /// recognize natively (no extra app install needed on the phone side -
    /// exactly like scanning to open a URL). Scanning this joins the
    /// Mobile Hotspot directly, which is the piece that used to require
    /// manually opening Windows Settings to read off the network name and
    /// password.
    /// </summary>
    public static string BuildWifiQrPayload(string ssid, string password)
    {
        // The spec (informally documented, but consistently implemented by
        // both platforms) requires backslash-escaping ';', ',', '"', and
        // '\' inside each field.
        static string Escape(string value) =>
            value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\"", "\\\"");

        string security = string.IsNullOrEmpty(password) ? "nopass" : "WPA";
        return $"WIFI:T:{security};S:{Escape(ssid)};P:{Escape(password)};;";
    }
}
