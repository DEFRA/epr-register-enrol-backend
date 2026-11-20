using EprRegisterEnrolBackend.AccreditationApplication.Services;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// RA-783: returns queued draws in turn, then DefaultDraw, so endpoint tests can predict a new
// accreditation number's sequence (DefaultDraw 583 gives 0583).
public sealed class FakeAccreditationSequenceRandom : IAccreditationSequenceRandom
{
    public const int DefaultDraw = 583;

    private readonly Queue<int> _draws = new();
    private readonly Lock _lock = new();

    public void Enqueue(params int[] draws)
    {
        lock (_lock)
            foreach (var draw in draws)
                _draws.Enqueue(draw);
    }

    public void Clear()
    {
        lock (_lock)
            _draws.Clear();
    }

    public int NextSequenceDigits()
    {
        lock (_lock)
            return _draws.Count > 0 ? _draws.Dequeue() : DefaultDraw;
    }
}
