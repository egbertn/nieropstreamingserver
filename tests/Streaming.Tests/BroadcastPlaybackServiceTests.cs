namespace Streaming.Tests;

public sealed class BroadcastPlaybackServiceTests
{
    [Fact]
    public void Shuffle_UsesFisherYatesWithTheProvidedRandomSource()
    {
        var queue = new[] { "first.mp3", "second.mp3", "third.mp3", "fourth.mp3" };
        var random = new ScriptedRandomSource(1, 0, 1);

        BroadcastPlaybackService.Shuffle(queue, random);

        Assert.Equal(["third.mp3", "fourth.mp3", "first.mp3", "second.mp3"], queue);
        Assert.Equal([4, 3, 2], random.MaximumValues);
    }

    private sealed class ScriptedRandomSource(params int[] values) : IRandomSource
    {
        private readonly Queue<int> _values = new(values);

        public List<int> MaximumValues { get; } = [];

        public int Next(int maxValue)
        {
            MaximumValues.Add(maxValue);
            return _values.Dequeue();
        }
    }
}