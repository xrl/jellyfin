using System;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Api.Extensions;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Audio compatibility policy for the official iOS client's video playback.
/// Client names are compatibility hints, not authentication or permission grants.
/// </summary>
internal static class IosVideoAudioPolicy
{
    internal static bool Applies(ClaimsPrincipal principal)
        => string.Equals(principal.GetClient(), "Jellyfin iOS", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCompatible(MediaStream? audio)
        => audio is null || (string.Equals(audio.Codec, "aac", StringComparison.OrdinalIgnoreCase)
            && audio.Channels is > 0 and <= 2);

    internal static bool CanServeStatically(MediaSourceInfo source)
        => source.MediaStreams.Where(stream => stream.Type == MediaStreamType.Audio).All(IsCompatible);

    internal static DeviceProfile RestrictProfile(DeviceProfile profile)
    {
        // Capabilities may be cached and shared by sessions. Never edit the supplied profile.
        var result = JsonSerializer.Deserialize<DeviceProfile>(JsonSerializer.SerializeToUtf8Bytes(profile))!;
        foreach (var direct in result.DirectPlayProfiles.Where(p => p.Type == DlnaProfileType.Video))
        {
            direct.AudioCodec = "aac";
        }

        foreach (var transcode in result.TranscodingProfiles.Where(p => p.Type == DlnaProfileType.Video))
        {
            transcode.AudioCodec = "aac";
            transcode.MaxAudioChannels = "2";
        }

        result.CodecProfiles = result.CodecProfiles.Append(new CodecProfile
        {
            Type = CodecType.VideoAudio,
            Codec = "aac",
            Conditions = [new ProfileCondition(ProfileConditionType.LessThanEqual, ProfileConditionValue.AudioChannels, "2", true)]
        }).ToArray();
        return result;
    }

    internal static void PrepareStream(StreamState state)
    {
        // Static responses return the whole source, not just the selected audio track.
        if (state.Request.Static && !CanServeStatically(state.MediaSource))
        {
            throw new ArgumentException("Jellyfin iOS video playback requires a dynamic AAC stereo stream.");
        }

        if (!IsCompatible(state.AudioStream)
            && (state.User is null || !state.User.HasPermission(PermissionKind.EnableAudioPlaybackTranscoding)))
        {
            throw new SecurityException("Audio transcoding is not permitted for this user.");
        }

        state.Request.AudioCodec = "aac";
        state.SupportedAudioCodecs = ["aac"];
        state.Request.MaxAudioChannels = Math.Min(state.Request.MaxAudioChannels ?? 2, 2);
        state.Request.TranscodingMaxAudioChannels = Math.Min(state.Request.TranscodingMaxAudioChannels ?? 2, 2);
        if (state.Request.AudioChannels.HasValue)
        {
            state.Request.AudioChannels = Math.Min(state.Request.AudioChannels.Value, 2);
        }

        if (!IsCompatible(state.AudioStream))
        {
            state.Request.AllowAudioStreamCopy = false;
        }
    }

    internal static void EnforceOutput(StreamState state)
    {
        // TryStreamCopy deliberately falls back to copy when permissions prohibit encoding.
        // PrepareStream rejects that case; this final guard also defeats explicit codec=copy.
        if (!IsCompatible(state.AudioStream))
        {
            state.OutputAudioCodec = "aac";
        }

        if (state.AudioStream is not null)
        {
            state.OutputAudioChannels = Math.Min(state.OutputAudioChannels ?? 2, 2);
        }
    }
}
