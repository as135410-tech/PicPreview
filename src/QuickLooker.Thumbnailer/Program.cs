using QuickLooker.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

try
{
    return await RunAsync(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0)
    {
        PrintUsage();
        return 1;
    }

    var command = args[0].ToLowerInvariant();
    var options = ParseOptions(args.Skip(1));

    return command switch
    {
        "thumbnail" => await CreateThumbnailAsync(options),
        "preview" => await CreatePreviewAsync(options),
        "warm" => await WarmFolderAsync(options),
        _ => UnknownCommand(command)
    };
}

static async Task<int> CreateThumbnailAsync(IReadOnlyDictionary<string, string> options)
{
    var input = Required(options, "input");
    var output = Required(options, "output");
    var size = OptionalInt(options, "size", 256);

    await ImageRenderer.RenderThumbnailAsync(input, output, size);
    Console.WriteLine(output);
    return 0;
}

static async Task<int> CreatePreviewAsync(IReadOnlyDictionary<string, string> options)
{
    var input = Required(options, "input");
    var output = Required(options, "output");
    var size = OptionalInt(options, "size", 4096);

    var bytes = await ImageRenderer.RenderPreviewPngAsync(input, size);
    await File.WriteAllBytesAsync(output, bytes);
    Console.WriteLine(output);
    return 0;
}

static async Task<int> WarmFolderAsync(IReadOnlyDictionary<string, string> options)
{
    var folder = Required(options, "folder");
    var size = OptionalInt(options, "size", 256);
    var recursive = options.ContainsKey("recursive");
    var cache = new ThumbnailCache();
    var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    var total = 0;

    foreach (var file in Directory.EnumerateFiles(folder, "*", searchOption).Where(SupportedImageFormats.IsSupported))
    {
        await cache.GetOrCreateAsync(file, size);
        total++;
        Console.WriteLine($"{total}: {file}");
    }

    Console.WriteLine($"已生成 {total} 个缩略图。");
    return 0;
}

static IReadOnlyDictionary<string, string> ParseOptions(IEnumerable<string> args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var pendingKey = string.Empty;

    foreach (var arg in args)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            pendingKey = arg[2..];
            result[pendingKey] = "true";
            continue;
        }

        if (pendingKey.Length == 0)
        {
            throw new ArgumentException($"无法识别参数：{arg}");
        }

        result[pendingKey] = arg;
        pendingKey = string.Empty;
    }

    return result;
}

static string Required(IReadOnlyDictionary<string, string> options, string name)
{
    if (options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
    {
        return value;
    }

    throw new ArgumentException($"缺少参数 --{name}");
}

static int OptionalInt(IReadOnlyDictionary<string, string> options, string name, int fallback)
{
    return options.TryGetValue(name, out var value) && int.TryParse(value, out var parsed)
        ? parsed
        : fallback;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"未知命令：{command}");
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        PicPreview thumbnailer

        thumbnail --input <图片路径> --output <png输出路径> [--size 256]
        preview   --input <图片路径> --output <png输出路径> [--size 4096]
        warm      --folder <文件夹路径> [--size 256] [--recursive]
        """);
}
