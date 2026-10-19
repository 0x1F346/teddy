using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;
using TonieFile;
using Xunit;

namespace TonieAudio.Tests
{
    public class DumpInfoTests : IDisposable
    {
        private readonly string Dir = TestAudio.NewTempDir();
        private readonly string TonieFilePath;
        private readonly string HashString;

        public DumpInfoTests()
        {
            string ogg = TestAudio.CreateOpusOgg(Dir, "sine.ogg", 1);
            var generated = new TonieFile.TonieAudio(new[] { ogg }, 0x5E034216, 96000);
            TonieFilePath = Path.Combine(Dir, "500304E0");
            File.WriteAllBytes(TonieFilePath, generated.FileContent);
            HashString = BitConverter.ToString(generated.Header.Hash).Replace("-", "");
        }

        public void Dispose()
        {
            Directory.Delete(Dir, true);
        }

        [Fact]
        public void Json_Output_Parses()
        {
            /* two entries the way Program.cs emits them: [ entry , entry ] -
               the old code prefixed every entry with a comma and never parsed */
            var entry = new StringBuilder();
            Assert.True(TonieTools.DumpInfo(entry, TonieTools.eDumpFormat.FormatJSON, TonieFilePath, new TonieTools.TonieData[0]));

            string document = "[\n" + entry + ",\n" + entry + "]";
            var array = (JArray)JToken.Parse(document);

            Assert.Equal(2, array.Count);
            Assert.Equal("5E034216", (string)array[0]["audio_id"]);
            Assert.Equal("TRUE", (string)array[0]["audio_hash_check"]);

            /* chapters start at page 0 already; no extra 0 may be prepended */
            var chapters = (JArray)array[0]["chapters"];
            Assert.Equal(0, (int)chapters[0]);
            Assert.True(chapters.Count < 2 || (int)chapters[1] != 0);
        }

        [Fact]
        public void Json_Failure_IsValidEntry()
        {
            string garbage = Path.Combine(Dir, "garbage.tonie");
            File.WriteAllBytes(garbage, new byte[0x3000]);

            var entry = new StringBuilder();
            Assert.False(TonieTools.DumpInfo(entry, TonieTools.eDumpFormat.FormatJSON, garbage, new TonieTools.TonieData[0]));

            var array = (JArray)JToken.Parse("[\n" + entry + "]");
            Assert.Equal(garbage, (string)array[0]["file"]);
            Assert.NotNull((string)array[0]["error"]);
        }

        [Fact]
        public void TextDump_DoesNotMutateSharedTonieData()
        {
            /* hash matches, but the audio ID is missing from the entry: used to index Hash[-1]
               and resize/write the shared array */
            var info = new TonieTools.TonieData
            {
                Title = "Test Tonie",
                Model = "01-0001",
                AudioId_ = new string[0],
                Hash = new[] { HashString, "AAAA" },
                Tracks = new[] { "Track one" },
            };

            var message = new StringBuilder();
            Assert.True(TonieTools.DumpInfo(message, TonieTools.eDumpFormat.FormatText, TonieFilePath, new[] { info }));

            Assert.Contains("Test Tonie", message.ToString());
            Assert.Equal(new[] { HashString, "AAAA" }, info.Hash);
        }
    }
}
