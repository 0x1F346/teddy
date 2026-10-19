using System;
using System.Linq;
using TonieFile;
using Xunit;

namespace TonieAudio.Tests
{
    public class ProtoCoderTests
    {
        /* FileHeader protobuf layout: 1 Hash (bytes), 2 AudioLength, 3 AudioId,
           4 AudioChapters (packed), 5 Padding (bytes), 6 Usable (bool, SkipEncode) */

        private static TonieFile.TonieAudio.FileHeader NewHeader()
        {
            return new TonieFile.TonieAudio.FileHeader
            {
                Hash = Enumerable.Range(0, 20).Select(v => (byte)v).ToArray(),
                AudioLength = 0x123456,
                AudioId = 0x5E034216,
                AudioChapters = new uint[] { 0, 17, 93 },
                Padding = new byte[] { 0, 0, 0 },
            };
        }

        [Fact]
        public void FileHeader_RoundTrip()
        {
            var header = NewHeader();
            var coder = new ProtoCoder();

            byte[] data = coder.Serialize(header);
            var parsed = new ProtoCoder().Deserialize<TonieFile.TonieAudio.FileHeader>(data);

            Assert.Equal(header.Hash, parsed.Hash);
            Assert.Equal(header.AudioLength, parsed.AudioLength);
            Assert.Equal(header.AudioId, parsed.AudioId);
            Assert.Equal(header.AudioChapters, parsed.AudioChapters);
            Assert.Equal(header.Padding, parsed.Padding);
        }

        [Fact]
        public void Boolean_Field_Decodes()
        {
            /* field 6 (Usable), wire type variant: tag 0x30 - sfx.bin sets this to 0.
               bool's Type.Name is "Boolean"; the decoder used to match on "Bool" and never set it */
            var parsed = new ProtoCoder().Deserialize<TonieFile.TonieAudio.FileHeader>(new byte[] { 0x30, 0x00 });
            Assert.False(parsed.Usable);

            parsed = new ProtoCoder().Deserialize<TonieFile.TonieAudio.FileHeader>(new byte[] { 0x30, 0x01 });
            Assert.True(parsed.Usable);
        }

        [Fact]
        public void Fixed32AndFixed64_Fields_AreSkipped()
        {
            /* field 2 as fixed64 (tag 0x11) and field 3 as fixed32 (tag 0x1D) must be consumed,
               otherwise the parser desyncs and misreads the following field */
            byte[] data =
            {
                0x11, 1, 2, 3, 4, 5, 6, 7, 8,  /* field 2, fixed64 */
                0x1D, 1, 2, 3, 4,              /* field 3, fixed32 */
                0x30, 0x01,                    /* field 6, variant: Usable = true */
            };

            var parsed = new ProtoCoder().Deserialize<TonieFile.TonieAudio.FileHeader>(data);
            Assert.True(parsed.Usable);
        }

        [Fact]
        public void OutOfOrder_Fields_AreRejected()
        {
            /* field 3 followed by field 2 - the order check used to be dead because
               lastField was never updated */
            byte[] data =
            {
                0x18, 0x01,  /* field 3, variant */
                0x10, 0x01,  /* field 2, variant */
            };

            Assert.Throws<FormatException>(() => new ProtoCoder().Deserialize<TonieFile.TonieAudio.FileHeader>(data));
        }
    }
}
