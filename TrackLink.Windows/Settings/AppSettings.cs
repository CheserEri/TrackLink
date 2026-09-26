using System.IO;
using System.Text.Json;

namespace TrackLink.Windows.Settings;

/// <summary>
/// 应用配置：触控板指纹（计划书要求「保存设备路径指纹；下次启动自动匹配」）+ 手势参数。
/// 存放于 <c>%LOCALAPPDATA%\TrackLink\settings.json</c>。
/// </summary>
internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackLink");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    /// <summary>上次选定的触控板厂商名（仅用于界面展示）。</summary>
    public string? TouchpadManufacturer { get; set; }

    /// <summary>上次选定的触控板产品名（仅用于界面展示）。</summary>
    public string? TouchpadProduct { get; set; }

    /// <summary>设备路径指纹的主体：分组键。下次启动按它自动匹配。</summary>
    public string? TouchpadGroupKey { get; set; }

    /// <summary>完整设备路径，用于匹配失败时给出更具体的提示。</summary>
    public string? TouchpadPath { get; set; }

    public GestureOptions Gesture { get; set; } = new();

    /// <summary>配置文件路径，供界面显示。</summary>
    public static string Location => FilePath;

    /// <summary>读取配置；文件不存在或损坏时返回默认值（不会抛异常，缺失设置不该拦住启动）。</summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is null)
            {
                return new AppSettings();
            }

            // Gesture 可能整体缺失（旧版本文件或手工编辑），兜底成默认值。
            loaded.Gesture ??= new GestureOptions();
            return loaded;
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    /// <summary>写回配置。失败时返回 false 并给出原因，由界面提示。</summary>
    public bool Save(out string? error)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}