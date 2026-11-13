using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace PDF_simple_edit.Services;

internal static class ClipboardObjectData
{
    public static void Set<T>(DataPackage package, string format, T value)
    {
        var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value), writable: false);
        package.SetData(format, stream.AsRandomAccessStream());
    }

    public static async Task<T?> ReadAsync<T>(DataPackageView view, string format)
    {
        object data = await view.GetDataAsync(format);
        if (data is string json)
            return JsonSerializer.Deserialize<T>(json);
        if (data is not IRandomAccessStream randomAccess)
            throw new InvalidDataException("Unsupported clipboard data.");

        using (randomAccess)
        using (var stream = randomAccess.AsStreamForRead())
        {
            // Windows can expose the previous string-based format as a
            // UTF-16LE stream with a terminating null and no byte order mark.
            Encoding encoding = stream.ReadByte() is '{' or '[' && stream.ReadByte() == 0
                ? Encoding.Unicode : Encoding.UTF8;
            stream.Position = 0;
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
            return JsonSerializer.Deserialize<T>((await reader.ReadToEndAsync()).TrimEnd('\0'));
        }
    }
}
