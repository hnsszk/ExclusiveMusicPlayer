using System.IO;
using System.Text.Json;
using ExclusiveMusicPlayer.Models;

namespace ExclusiveMusicPlayer.Services;

/// <summary>
/// 喜欢的音乐本地缓存。存到 %APPDATA%\ExclusiveMusicPlayer\liked_songs_cache.json。
/// 打开「喜欢的音乐」时先显示缓存（秒开），后台再用最新 id 列表增量刷新。
/// 同时保存歌曲 id 顺序（即 /likelist 返回顺序，最近收藏在前），供排序使用。
/// </summary>
public sealed class LikedSongsCache
{
    private static readonly string CacheDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExclusiveMusicPlayer");

    private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "liked_songs_cache.json");

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>缓存的歌曲，顺序即收藏顺序（最近在前）。</summary>
    public IReadOnlyList<Song>? Songs { get; private set; }

    public bool HasCache => Songs is { Count: > 0 };

    /// <summary>保存缓存。</summary>
    public void Save(IReadOnlyList<Song> songs)
    {
        Songs = songs;

        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var record = new CacheRecord { Songs = songs.ToList() };
            File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(record, _jsonOptions));
        }
        catch (Exception)
        {
            // 缓存写入失败不阻断播放流程，下次重新拉取即可。
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
            Songs = record?.Songs;
        }
        catch (Exception)
        {
            // 缓存文件损坏时忽略，当作没有缓存。
            Songs = null;
        }
    }

    private sealed class CacheRecord
    {
        public List<Song>? Songs { get; init; }
    }
}
