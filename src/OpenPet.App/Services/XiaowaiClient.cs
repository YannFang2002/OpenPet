using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenPet.App.Services;

/// <summary>远端此刻状态（NAS 18900 /api/state 的只读快照）。</summary>
public sealed record XiaowaiView(
    string State,                 // chatting / browsing_bili / idle
    string Label,                 // 中文状态名
    string Detail,                // 一句话细节
    double SinceMin,
    bool Sleeping,
    double BiliTodayMin,
    double BiliTodayLimit,
    double? NextBiliInMin,
    double? NextProactiveInMin,
    string? VideoBvid,
    string? VideoTitle,
    int TodayMsgs,
    int MemoryCount,
    string? LastReply,
    int DaysTogether,
    string? BornAt,
    string[] RecentLines,
    string? Name);                // AstrBot 人格同步的名字；缺失为 null

public sealed class XiaowaiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public XiaowaiClient(string baseUrl, double timeoutSec = 3)
    {
        _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 1, 30)) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenPet/1.0");
    }

    public async Task<XiaowaiView?> FetchAsync(CancellationToken ct = default)
    {
        if (_baseUrl.Length == 0) return null;
        try
        {
            using HttpResponseMessage resp = await _http.GetAsync(_baseUrl + "/api/state", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("activity", out JsonElement act) ||
                act.ValueKind != JsonValueKind.Object) return null;

            static string S(JsonElement e, string n) =>
                e.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
            static double D(JsonElement e, string n) =>
                e.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            static double? DN(JsonElement e, string n) =>
                e.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null;
            static bool B(JsonElement e, string n) =>
                e.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.True;

            string? bvid = null, title = null;
            if (act.TryGetProperty("last_video", out JsonElement vid) && vid.ValueKind == JsonValueKind.Object)
            {
                string b = S(vid, "bvid"), t = S(vid, "title");
                bvid = string.IsNullOrWhiteSpace(b) ? null : b;
                title = string.IsNullOrWhiteSpace(t) ? null : t;
            }

            int todayMsgs = 0, memory = 0, daysTogether = 0;
            string? lastReply = null, bornAt = null, name = null;
            string[] recentLines = Array.Empty<string>();
            if (doc.RootElement.TryGetProperty("xiaowai", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object)
            {
                string nm = S(meta, "name");
                name = string.IsNullOrWhiteSpace(nm) ? null : nm.Trim();
                todayMsgs = (int)D(meta, "today_msgs");
                memory = (int)D(meta, "memory_count");
                daysTogether = (int)D(meta, "days_together");
                string lr = S(meta, "last_reply");
                lastReply = string.IsNullOrWhiteSpace(lr) ? null : lr;
                string born = S(meta, "born_at");
                bornAt = string.IsNullOrWhiteSpace(born) ? null : born;
                if (meta.TryGetProperty("recent_lines", out JsonElement recent) &&
                    recent.ValueKind == JsonValueKind.Array)
                {
                    var lines = new List<string>();
                    foreach (JsonElement item in recent.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        string text = item.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            lines.Add(text);
                        }
                    }

                    recentLines = lines.ToArray();
                }
            }

            string state = S(act, "state");
            return new XiaowaiView(
                state.Length == 0 ? "idle" : state,
                S(act, "label"), S(act, "detail"),
                D(act, "since_min"), B(act, "sleeping"),
                D(act, "bili_today_min"), D(act, "bili_today_limit"),
                DN(act, "next_bili_in_min"), DN(act, "next_proactive_in_min"),
                bvid, title, todayMsgs, memory, lastReply,
                daysTogether, bornAt, recentLines, name);
        }
        catch
        {
            return null;   // 网络/解析问题一律当成「这次没拿到」，上层退避重试
        }
    }

    public async Task<byte[]?> FetchCoverAsync(string bvid, CancellationToken ct = default)
    {
        if (_baseUrl.Length == 0 || string.IsNullOrWhiteSpace(bvid)) return null;
        try
        {
            using HttpResponseMessage resp = await _http
                .GetAsync(_baseUrl + "/api/cover?b=" + Uri.EscapeDataString(bvid), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return bytes.Length > 512 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
