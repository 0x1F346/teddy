using System;
using System.Linq;
using TeddyBench;
using Xunit;

namespace TonieAudio.Tests
{
    public class Iso15693Tests
    {
        [Fact]
        public void BuildCommand_ChecksumValidates()
        {
            byte[] command = ISO15693.BuildCommand(ISO15693.Command.INVENTORY, new byte[] { 0x00 });

            Assert.True(ISO15693.CheckChecksum(command));

            command[1] ^= 0xFF;
            Assert.False(ISO15693.CheckChecksum(command));
        }

        [Fact]
        public void CheckChecksum_WithOffset()
        {
            /* a response prefixed with unrelated bytes: the CRC stays in the last two bytes,
               start only moves the beginning of the checksummed data */
            byte[] command = ISO15693.BuildCommand(ISO15693.Command.READBLOCK, 0x0102030405060708uL, new byte[] { 0x01 });
            byte[] prefixed = new byte[] { 0xDE, 0xAD, 0xBE }.Concat(command).ToArray();

            Assert.True(ISO15693.CheckChecksum(command));
            Assert.True(ISO15693.CheckChecksum(prefixed, 3));
            Assert.False(ISO15693.CheckChecksum(prefixed));
        }
    }
}
