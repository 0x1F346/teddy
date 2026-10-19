using System;
using System.IO;
using System.Linq;
using TonieFile;
using Xunit;

namespace TonieAudio.Tests
{
    public class EncodeTests : IDisposable
    {
        private readonly string Dir = TestAudio.NewTempDir();

        public void Dispose()
        {
            Directory.Delete(Dir, true);
        }

        private TonieFile.TonieAudio Reparse(TonieFile.TonieAudio generated)
        {
            string path = Path.Combine(Dir, "generated.tonie");
            File.WriteAllBytes(path, generated.FileContent);
            return TonieFile.TonieAudio.FromFile(path);
        }

        [Fact]
        public void Encode_OggInput_DefaultBitrate_Succeeds()
        {
            /* regression for the ogg bitrate being passed to the decoder as its sample rate:
               every .ogg source failed at the CLI default of 96 kbps */
            string ogg = TestAudio.CreateOpusOgg(Dir, "sine.ogg", 2);

            var generated = new TonieFile.TonieAudio(new[] { ogg }, 0x11111111, 96000);
            var parsed = Reparse(generated);

            Assert.True(parsed.HashCorrect);
            Assert.InRange(TestAudio.DecodedSeconds(parsed.Audio), 1.5, 2.5);
        }

        [Fact]
        public void Encode_Mp3_RoundTrip()
        {
            string[] sources =
            {
                TestAudio.FixturePath("sine440-mono-22k.mp3"),
                TestAudio.FixturePath("sine440-stereo-44k.mp3"),
            };

            var generated = new TonieFile.TonieAudio(sources, 0x22222222, 96000);
            var parsed = Reparse(generated);

            Assert.True(parsed.HashCorrect);
            Assert.Equal(0x22222222u, parsed.Header.AudioId);
            Assert.Equal(parsed.Audio.Length, parsed.Header.AudioLength);
            Assert.Equal(2, parsed.Header.AudioChapters.Length);
            Assert.Equal(0u, parsed.Header.AudioChapters[0]);
            /* two 1 s tracks; MP3 decoder padding and silence skipping allow some slack */
            Assert.InRange(TestAudio.DecodedSeconds(parsed.Audio), 1.5, 2.6);
        }

        [Fact]
        public void Encode_MonoNon48k_ProducesValid48kStereo()
        {
            /* the mono 22.05 kHz fixture exercises the resample + upmix chain;
               a broken chain would stretch or shrink the duration */
            string[] sources = { TestAudio.FixturePath("sine440-mono-22k.mp3") };

            var generated = new TonieFile.TonieAudio(sources, 0x33333333, 96000);
            var parsed = Reparse(generated);

            Assert.True(parsed.HashCorrect);
            Assert.InRange(TestAudio.DecodedSeconds(parsed.Audio), 0.75, 1.3);
        }

        [Fact]
        public void Encode_Ogg_TwoTracks_ChaptersOrdered()
        {
            string[] sources =
            {
                TestAudio.CreateOpusOgg(Dir, "01.ogg", 1, 440f),
                TestAudio.CreateOpusOgg(Dir, "02.ogg", 1, 880f),
            };

            var generated = new TonieFile.TonieAudio(sources, 0x44444444, 96000);
            var parsed = Reparse(generated);

            Assert.True(parsed.HashCorrect);
            Assert.Equal(2, parsed.Header.AudioChapters.Length);
            Assert.True(parsed.Header.AudioChapters.SequenceEqual(parsed.Header.AudioChapters.OrderBy(c => c)));
            Assert.Equal(0u, parsed.Header.AudioChapters[0]);
            Assert.NotEqual(0u, parsed.Header.AudioChapters[1]);
        }
    }
}
