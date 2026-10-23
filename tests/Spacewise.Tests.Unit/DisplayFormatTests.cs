using Spacewise.Core;

namespace Spacewise.Tests.Unit;

public class DisplayFormatTests
{
    [Theory]
    [InlineData(0L, "0\u00A0байт")]
    [InlineData(17L, "17\u00A0байт")]
    [InlineData(1023L, "1\u202F023\u00A0байт")]
    [InlineData(1024L, "1,0\u00A0КБ")]
    [InlineData(102_348L, "99,9\u00A0КБ")]
    [InlineData(102_349L, "100\u00A0КБ")]
    [InlineData(1_048_575L, "1,0\u00A0МБ")]
    [InlineData(5_153_960_755L, "4,8\u00A0ГБ")]
    [InlineData(214L << 30, "214\u00A0ГБ")]
    [InlineData(3L << 40, "3,0\u00A0ТБ")]
    public void Size_follows_unit_rule(long bytes, string expected) => Assert.Equal(expected, DisplayFormat.Size(bytes));

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(999L, "999")]
    [InlineData(12_840L, "12\u202F840")]
    [InlineData(1_245_802L, "1\u202F245\u202F802")]
    public void Count_uses_narrow_space_thousands(long value, string expected) => Assert.Equal(expected, DisplayFormat.Count(value));
}
