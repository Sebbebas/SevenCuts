namespace SevenCuts
{
    public class Segment
    {
        public long StartMs { get; set; }
        public long EndMs { get; set; }
        public bool IsSelected { get; set; }
        public long DurationMs => EndMs - StartMs;
    }
}