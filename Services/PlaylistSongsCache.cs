using System.IO;
using System.Text.Json;
using ExclusiveMusicPlayer.Models;

namespace ExclusiveMusicPlayer.Services;

/// <summary>
/// 我创建的歌单曲目缓存。存到 %APPDATA%\ExclusiveMusicPlayer\playlists_cache.json，
/// 与「我喜欢的音乐」缓存（liked_songs_cache.json）完全独立，绝不混用。
/// 打开自建歌单时先显示该歌单自己的缓存（秒开），后台再拉最新曲目替换。
/// 只缓存用户自建歌单；搜索/收藏的歌单、日推、专辑、歌手等不缓存。
/// 同一账号下按歌单 id 分键，每个歌单维护自己的歌曲顺序。
/// </summary>
public sealed class PlaylistSongsCache
{
    private static readonly string CacheDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExclusiveMusicPlayer");

    private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "playlists_cache.json");

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private Dictionary<long, List<Song>> _byPlaylistId = new();

    /// <summary>某个歌单是否有缓存。</summary>
    public bool HasCache(long playlistId) => _byPlaylistId.TryGetValue(playlistId, out var songs) && songs is { Count: > 0 };

    /// <summary>读取某个歌单的缓存曲目（无缓存返回 null）。</summary>
    public IReadOnlyList<Song>? Get(long playlistId)
        => _byPlaylistId.TryGetValue(playlistId, out var songs) ? songs : null;

    /// <summary>保存某个歌单的曲目缓存（独立于其他歌单）。</summary>
    public void Save(long playlistId, IReadOnlyList<Song> songs)
    {
        _byPlaylistId[playlistId] = songs.ToList();
        Persist();
    }

    /// <summary>从缓存中移除某首歌（用于删除歌曲后同步，避免下次打开缓存里还有它）。</summary>
    public void RemoveSong(long playlistId, long songId)
    {
        if (!_byPlaylistId.TryGetValue(playlistId, out var songs))
        {
            return;
        }

        if (songs.RemoveAll(s => s.Id == songId) > 0)
        {
            Persist();
        }
    }

    /// <summary>从磁盘加载全部歌单缓存。</summary>
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
            _byPlaylistId = record?.Playlists is { Count: > 0 }
                ? record.Playlists.Where(p => p.Key > 0 && p.Value is { Count: > 0 })
                    .ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<long, List<Song>>();
        }
        catch (Exception)
        {
            // 缓存文件损坏时当作没有缓存，下次重新拉取即可。
            _byPlaylistId = new Dictionary<long, List<Song>>();
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var record = new CacheRecord
            {
                Playlists = _byPlaylistId.ToDictionary(kv => kv.Key, kv => kv.Value),
            };
            File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(record, _jsonOptions));
        }
        catch (Exception)
        {
            // 缓存写入失败不阻断播放流程，下次重新拉取即可。
        }
    }

    private sealed class CacheRecord
    {
        public Dictionary<long, List<Song>>? Playlists { get; init; }
    }
}
