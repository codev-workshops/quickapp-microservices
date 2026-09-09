namespace Identity.Migration;

/// <summary>
/// SQL Server datetime2/datetimeoffset carry 100ns ticks; Postgres timestamps only microseconds. The Debezium JDBC
/// sink binds timestamps through pgjdbc, which rounds nanoseconds half-up to microseconds before sending them, so
/// every path that writes or compares a SQL Server timestamp against identitydb applies the same rounding and
/// backfilled, CDC-replicated and diffed values agree tick-for-tick.
/// </summary>
public static class Microseconds
{
    public static DateTime Round(DateTime value)
    {
        var remainder = value.Ticks % 10;
        var ticks = value.Ticks - remainder + (remainder >= 5 ? 10 : 0);
        return new DateTime(ticks, value.Kind);
    }

    public static DateTimeOffset Round(DateTimeOffset value) =>
        new(Round(value.DateTime), value.Offset);
}
