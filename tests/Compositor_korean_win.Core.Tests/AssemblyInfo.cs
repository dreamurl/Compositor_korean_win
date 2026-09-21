// PixelBuffer.LiveBytes is process-wide, and several tests assert on how it moves. Running test
// classes in parallel would let one allocation land inside another's measurement.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
