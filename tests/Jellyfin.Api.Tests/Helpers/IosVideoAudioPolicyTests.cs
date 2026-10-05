using System;
using System.Collections.Generic;
using System.Net;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Helpers;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers;

public class IosVideoAudioPolicyTests
{
    [Theory]
    [InlineData("Jellyfin iOS", true)]
    [InlineData("jellyfin ios", true)]
    [InlineData("Jellyfin Web", false)]
    [InlineData("Jellyfin Android", false)]
    [InlineData("Safari", false)]
    [InlineData("Jellyfin iPadOS", false)]
    [InlineData("Jellyfin iOS unofficial", false)]
    [InlineData(null, false)]
    public void Applies_OnlyExplicitOfficialClient(string? client, bool expected)
        => Assert.Equal(expected, IosVideoAudioPolicy.Applies(Principal(client)));

    [Theory]
    [InlineData(MediaStreamProtocol.http)]
    [InlineData(MediaStreamProtocol.hls)]
    public void SetDeviceSpecificData_Eac3_OffersAacStereoWithoutMutatingProfile(MediaStreamProtocol protocol)
    {
        var profile = Profile(protocol);
        var source = Source("eac3", 6);
        var user = CreateUser(true);
        var helper = MediaHelper(user);

        Negotiate(helper, source, profile, Principal("Jellyfin iOS"), user);

        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.NotNull(source.TranscodingUrl);
        Assert.Contains("AudioCodec=aac", source.TranscodingUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MaxAudioChannels=2", source.TranscodingUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("aac,eac3", profile.DirectPlayProfiles[0].AudioCodec);
        Assert.Equal("aac,eac3", profile.TranscodingProfiles[0].AudioCodec);
        Assert.Empty(profile.CodecProfiles);

        var otherSource = Source("eac3", 6);
        Negotiate(helper, otherSource, profile, Principal("Jellyfin Android"), user);
        Assert.True(otherSource.SupportsDirectPlay);
    }

    [Fact]
    public void SetDeviceSpecificData_AacStereo_RemainsDirectPlayable()
    {
        var user = CreateUser(false);
        var source = Source("aac", 2);
        Negotiate(MediaHelper(user), source, Profile(MediaStreamProtocol.http), Principal("Jellyfin iOS"), user);
        Assert.True(source.SupportsDirectPlay);
        Assert.Null(source.TranscodingUrl);
    }

    [Fact]
    public void SetDeviceSpecificData_AudioPermissionUnavailable_DoesNotOfferEncoding()
    {
        var user = CreateUser(false);
        var source = Source("eac3", 6);
        Negotiate(MediaHelper(user), source, Profile(MediaStreamProtocol.http), Principal("Jellyfin iOS"), user);
        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.False(source.SupportsTranscoding);
        Assert.Null(source.TranscodingUrl);
    }

    [Theory]
    [InlineData(TranscodingJobType.Progressive, "eac3", 6, "aac", 2)]
    [InlineData(TranscodingJobType.Hls, "eac3", 6, "aac", 2)]
    [InlineData(TranscodingJobType.Progressive, "aac", 6, "aac", 2)]
    [InlineData(TranscodingJobType.Hls, "aac", 2, "copy", 2)]
    [InlineData(TranscodingJobType.Progressive, "aac", 2, "copy", 2)]
    public async Task GetStreamingState_Ios_EnforcesSelectedAudioAndPreservesVideoCopy(
        TranscodingJobType type, string codec, int channels, string outputCodec, int outputChannels)
    {
        var state = await StreamingState("Jellyfin iOS", codec, channels, type).ConfigureAwait(true);
        Assert.Equal(outputCodec, state.OutputAudioCodec);
        Assert.Equal(outputChannels, state.OutputAudioChannels);
        Assert.Equal("copy", state.OutputVideoCodec);
        Assert.False(state.User.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding));
    }

    [Fact]
    public async Task GetStreamingState_NonIos_Unchanged()
    {
        var state = await StreamingState("Jellyfin Android", "eac3", 6, TranscodingJobType.Progressive).ConfigureAwait(true);
        Assert.Equal("copy", state.OutputAudioCodec);
        Assert.Equal("copy", state.OutputVideoCodec);
    }

    [Fact]
    public async Task GetStreamingState_Ios_NoAudioPermission_RejectsRatherThanCopies()
    {
        await Assert.ThrowsAsync<SecurityException>(() => StreamingState("Jellyfin iOS", "eac3", 6, TranscodingJobType.Progressive, audioPermission: false));
    }

    [Theory]
    [InlineData("Jellyfin iOS", "eac3", 6, true)]
    [InlineData("Jellyfin iOS", "aac", 2, false)]
    [InlineData("Jellyfin Android", "eac3", 6, false)]
    public async Task GetStreamingState_Static_CannotBypassPolicy(string client, string codec, int channels, bool rejects)
    {
        if (rejects)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => StreamingState(client, codec, channels, TranscodingJobType.Progressive, isStatic: true));
        }
        else
        {
            var state = await StreamingState(client, codec, channels, TranscodingJobType.Progressive, isStatic: true).ConfigureAwait(true);
            Assert.True(state.Request.Static);
        }
    }

    [Fact]
    public async Task GetStreamingState_ExplicitCopyAndCodecChannelOptions_CannotBypassPolicy()
    {
        var state = await StreamingState("Jellyfin iOS", "eac3", 6, TranscodingJobType.Progressive, requestCopy: true).ConfigureAwait(true);
        Assert.Equal("aac", state.OutputAudioCodec);
        Assert.Equal(2, state.OutputAudioChannels);
        Assert.Equal("copy", state.OutputVideoCodec);
    }

    [Fact]
    public async Task GetStreamingState_SelectedSecondTrack_CannotCopyEac3()
    {
        var state = await StreamingState("Jellyfin iOS", "eac3", 6, TranscodingJobType.Hls, selectedSecondTrack: true).ConfigureAwait(true);
        Assert.Equal(2, state.AudioStream.Index);
        Assert.Equal("aac", state.OutputAudioCodec);
        Assert.Equal(2, state.OutputAudioChannels);
    }

    private static ClaimsPrincipal Principal(string? client, Guid userId = default)
    {
        var claims = new List<Claim>
        {
            new(InternalClaimTypes.UserId, userId.ToString()),
            new(InternalClaimTypes.DeviceId, "test-device"),
            new(InternalClaimTypes.Token, "test-token")
        };
        if (client is not null)
        {
            claims.Add(new Claim(InternalClaimTypes.Client, client));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static User CreateUser(bool audioPermission)
    {
        var user = new User("test", "test", "test");
        user.SetPermission(PermissionKind.EnableAudioPlaybackTranscoding, audioPermission);
        user.SetPermission(PermissionKind.EnableVideoPlaybackTranscoding, false);
        user.SetPermission(PermissionKind.EnablePlaybackRemuxing, true);
        return user;
    }

    private static MediaSourceInfo Source(string codec, int channels) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Path = "/test/video.mkv",
        Container = "mkv",
        Protocol = MediaProtocol.File,
        Bitrate = 2_000_000,
        MediaStreams =
        [
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Profile = "High", Level = 40, Width = 1920, Height = 1080, BitRate = 1_800_000, PixelFormat = "yuv420p" },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = codec, Channels = channels, BitRate = 192_000, SampleRate = 48000 }
        ],
        DefaultAudioStreamIndex = 1
    };

    private static DeviceProfile Profile(MediaStreamProtocol protocol) => new()
    {
        DirectPlayProfiles = [new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "mkv,mp4", VideoCodec = "h264", AudioCodec = "aac,eac3" }],
        TranscodingProfiles = [new TranscodingProfile { Type = DlnaProfileType.Video, Container = "mp4", VideoCodec = "h264", AudioCodec = "aac,eac3", Protocol = protocol, Context = EncodingContext.Streaming }]
    };

    private static IMediaEncoder Encoder()
    {
        var encoder = new Mock<IMediaEncoder>();
        encoder.Setup(e => e.CanEncodeToAudioCodec(It.IsAny<string>())).Returns(true);
        return encoder.Object;
    }

    private static MediaInfoHelper MediaHelper(User user)
    {
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns(user);
        var config = new Mock<IServerConfigurationManager>();
        config.SetupGet(m => m.Configuration).Returns(new ServerConfiguration());
        return new MediaInfoHelper(users.Object, Mock.Of<ILibraryManager>(), Mock.Of<IMediaSourceManager>(), Encoder(), config.Object,
            Mock.Of<ILogger<MediaInfoHelper>>(), Mock.Of<INetworkManager>(), Mock.Of<IDeviceManager>(), Mock.Of<IServerApplicationHost>());
    }

    private static void Negotiate(MediaInfoHelper helper, MediaSourceInfo source, DeviceProfile profile, ClaimsPrincipal principal, User user)
        => helper.SetDeviceSpecificData(new Movie { Id = Guid.NewGuid() }, source, profile, principal, null, 0, source.Id, 1, null, 6,
            "test-session", user.Id, true, true, true, true, true, false, IPAddress.Loopback);

    private static async Task<StreamState> StreamingState(string client, string codec, int channels, TranscodingJobType type,
        bool audioPermission = true, bool isStatic = false, bool selectedSecondTrack = false, bool requestCopy = false)
    {
        var item = new Movie { Id = Guid.NewGuid() };
        var user = CreateUser(audioPermission);
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns(user);
        var library = new Mock<ILibraryManager>();
        library.Setup(m => m.GetItemById<BaseItem>(item.Id)).Returns(item);
        var source = Source(codec, channels);
        if (selectedSecondTrack)
        {
            source.MediaStreams[1].Codec = "aac";
            source.MediaStreams[1].Channels = 2;
            source.MediaStreams = [.. source.MediaStreams, new MediaStream { Index = 2, Type = MediaStreamType.Audio, Codec = codec, Channels = channels, SampleRate = 48000 }];
        }

        var sources = new Mock<IMediaSourceManager>();
        sources.Setup(m => m.GetPlaybackMediaSources(It.IsAny<BaseItem>(), null, false, false, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { source });
        var config = new Mock<IServerConfigurationManager>();
        config.Setup(m => m.GetConfiguration("encoding")).Returns(new EncodingOptions { TranscodingTempPath = "/tmp" });
        config.SetupGet(m => m.CommonApplicationPaths).Returns(Mock.Of<IApplicationPaths>());
        var encoder = Encoder();
        var encoding = new EncodingHelper(Mock.Of<IApplicationPaths>(), encoder, Mock.Of<ISubtitleEncoder>(), Mock.Of<IConfiguration>(), config.Object, Mock.Of<IPathManager>());
        var context = new DefaultHttpContext { User = Principal(client, user.Id) };
        context.Request.Path = type == TranscodingJobType.Hls ? "/Videos/test/master.m3u8" : "/Videos/test/stream.mp4";
        if (requestCopy)
        {
            context.Request.QueryString = new QueryString("?aac-audiochannels=8");
        }

        var request = new VideoRequestDto
        {
            Id = item.Id,
            Static = isStatic,
            VideoCodec = "h264",
            AudioCodec = requestCopy ? "copy" : "eac3,aac",
            AudioStreamIndex = selectedSecondTrack ? 2 : 1,
            MaxAudioChannels = 6,
            AudioChannels = 6,
            TranscodingMaxAudioChannels = 6,
            EnableAutoStreamCopy = true,
            AllowAudioStreamCopy = true,
            AllowVideoStreamCopy = true
        };
        return await StreamingHelpers.GetStreamingState(request, context, sources.Object, users.Object, library.Object, config.Object, encoder,
            encoding, Mock.Of<ITranscodeManager>(), type, CancellationToken.None).ConfigureAwait(false);
    }
}
