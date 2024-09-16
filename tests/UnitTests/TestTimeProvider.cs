namespace UnitTests;

public class TestTimeProvider : TimeProvider
{
    public DateTime Now { get; set; } = DateTime.UtcNow;
    public void Advance(TimeSpan time) => Now += time;

    public override DateTimeOffset GetUtcNow() => Now;
}