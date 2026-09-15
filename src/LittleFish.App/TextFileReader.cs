using System.IO;
using System.Text;
using UtfUnknown;

namespace LittleFish.App;

internal sealed class TextFileOpenException(string message, Exception? inner = null) : Exception(message, inner);

internal static class TextFileReader
{
    public static string Read(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new TextFileOpenException("仅支持 TXT 文件。");
        }

        try
        {
            return Decode(File.ReadAllBytes(path));
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new TextFileOpenException("无权读取此文件。", exception);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            throw new TextFileOpenException("文件被占用，暂时无法打开。", exception);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new TextFileOpenException("文件不存在或已移动。", exception);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            throw new TextFileOpenException("无法读取此文件。", exception);
        }
    }

    internal static string Decode(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (bytes.Length == 0) return "";

        // Longer BOMs go first because UTF-32 LE begins with the UTF-16 LE BOM.
        Encoding[] unicodeEncodings =
        [
            new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
            new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true),
            new UnicodeEncoding(true, true, true)
        ];
        foreach (var encoding in unicodeEncodings)
        {
            var preamble = encoding.GetPreamble();
            if (bytes.AsSpan().StartsWith(preamble))
            {
                try
                {
                    return RequireText(encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length));
                }
                catch (DecoderFallbackException exception)
                {
                    throw new TextFileOpenException("文件编码无法识别。", exception);
                }
            }
        }

        try
        {
            var utf8 = new UTF8Encoding(false, true).GetString(bytes);
            if (!utf8.Contains('\0')) return RequireText(utf8);
        }
        catch (DecoderFallbackException)
        {
        }

        var detected = CharsetDetector.DetectFromBytes(bytes).Detected?.Encoding;
        var candidates = new[] { detected, Encoding.GetEncoding("GB18030"), Encoding.GetEncoding("Big5") };
        foreach (var encoding in candidates.Where(encoding => encoding is not null).DistinctBy(encoding => encoding!.CodePage))
        {
            try
            {
                var strict = Encoding.GetEncoding(encoding!.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return RequireText(strict.GetString(bytes));
            }
            catch (DecoderFallbackException)
            {
            }
        }
        throw new TextFileOpenException("文件编码无法识别。");
    }

    private static string RequireText(string text)
    {
        if (text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t' and not '\f'))
        {
            throw new TextFileOpenException("文件不是可阅读的文本。");
        }
        return text;
    }
}
