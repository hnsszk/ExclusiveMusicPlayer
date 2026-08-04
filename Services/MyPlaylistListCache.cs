using System.IO;
using System.Text.Json;
using ExclusiveMusicPlayer.Models;

namespace ExclusiveMusicPlayer.Services;

/// <summary>
/// 「我的歌单」列表元信息缓存。存到 %APPDATA%\ExclusiveMusicPlayer\my_playlists_cache.json。
/// 缓存我创建 + 我收藏的歌单的列表信息（缩略图/名称/歌曲数等，即 Playlist 的元信息，
/// 不含歌单内部歌曲列表——内部歌曲缓存请用 PlaylistSongsCache）。
/// 打开「我的歌单」页时先显示缓存（秒开、图片不重新加载），后台再拉最新元信息做增量对齐。
/// 注意：收藏的歌单与首页推荐歌单的【内部歌曲】不缓存（见 PlaylistSongsCache），
/// 但「我的歌单」列表本身的缩略图/名称属于本类缓存的范畴。
/// </summary>
public sealed class MyPlaylistListCache
{
    private static readonly string CacheDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExclusiveMusicPlayer");

    private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "my_playlists_cache.json");

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>缓存的我创建的歌单列表（保持服务端顺序）。</summary>
    public IReadOnlyList<Playlist>? Created { get; private set; }

    /// <summary>缓存的我收藏的歌单列表（保持服务端顺序）。</summary>
    public IReadOnlyList<Playlist>? Collected { get; private set; }

    public bool HasCache => (Created is { Count: > 0 }) || (Collected is { Count: > 0 });

    /// <summary>保存缓存（仅覆盖我的歌单列表元信息，不触碰歌曲缓存）。</summary>
    public void Save(IReadOnlyList<Playlist> created, IReadOnlyList<Playlist> collected)
    {
        Created = created.ToList();
        Collected = collected.ToList();

        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var record = new CacheRecord { Created = Created.ToList(), Collected = Collected.ToList() };
            File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(record, _jsonOptions));
        }
        catch (Exception)
        {
            // 缓存写入失败不阻断主流程，下次重新拉取即可。
        }
    }

    /// <summary>从磁盘加载缓存。</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(CacheFilePath))
            {
                return;
            }

            var record = JsonSerializer.Deserialize<CacheRecord>(
                File.ReadAllText(CacheFilePath),
                _jsonOptions);
            Created = record?.Created is { Count: > 0 } ? record.Created : null;
            Collected = record?.Collected is { Count: > 0 } ? record.Collected : null;
        }
        catch (Exception)
        {
            // 缓存文件损坏时当作没有缓存。
            Created = null;
            Collected = null;
        }
    }

    /// <summary>清空缓存（登出时调用，换账号不串）。</summary>
    public void Clear()
    {
        Created = null;
        Collected = null;

        try
        {
            if (File.Exists(CacheFilePath))
            {
                File.Delete(CacheFilePath);
            }
        }
        catch (Exception)
        {
            // 删除失败下次覆盖即可。
        }
    }

    private sealed class CacheRecord
    {
        public List<Playlist>? Created { get; init; }

        public List<Playlist>? Collected { get; init; }
    }
}
