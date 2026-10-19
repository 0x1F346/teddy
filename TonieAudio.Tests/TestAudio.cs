using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using System;
using System.IO;

namespace TonieAudio.Tests
{
    /* helpers to synthesize audio inputs and inspect encoded output */
    public static class TestAudio
    {
        public static string FixturePath(string name)
        {
            return Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        }

        public static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "TonieAudio.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /* write a sine wave as an Ogg/Opus file using the same vendored Concentus code the product uses */
        public static string CreateOpusOgg(string directory, string name, double seconds, float frequency = 440f)
        {
            string path = Path.Combine(directory, name);
            var encoder = OpusEncoder.Create(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
            encoder.Bitrate = 96000;

            using (var stream = File.Create(path))
            {
                var ogg = new OpusOggWriteStream(encoder, stream, new OpusTags(), 48000, 0x12345678);
                int totalSamples = (int)(seconds * 48000);
                float[] buffer = new float[totalSamples * 2];

                for (int sample = 0; sample < totalSamples; sample++)
                {
                    float value = 0.5f * (float)Math.Sin(2 * Math.PI * frequency * sample / 48000.0);
                    buffer[2 * sample + 0] = value;
                    buffer[2 * sample + 1] = value;
                }

                ogg.WriteSamples(buffer, 0, buffer.Length);
                ogg.Finish();
            }

            return path;
        }

        /* decode the Opus payload of an encoded Tonie file and return its duration in seconds */
        public static double DecodedSeconds(byte[] audio)
        {
            using (var stream = new MemoryStream(audio))
            {
                var read = new OpusOggReadStream(OpusDecoder.Create(48000, 2), stream);
                long samplesPerChannel = 0;

                while (read.HasNextPacket)
                {
                    short[] packet = read.DecodeNextPacket();
                    if (packet == null)
                    {
                        break;
                    }
                    samplesPerChannel += packet.Length / 2;
                }

                return samplesPerChannel / 48000.0;
            }
        }
    }
}
