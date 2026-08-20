namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-448: shared signal between RegulatoryNumberSequenceBackfillService (writer)
// and RegulatoryNumberBackfillHealthCheck (reader) - registered as a singleton so
// both resolve the same instance.
public interface IRegulatoryNumberBackfillStatus
{
    bool IsComplete { get; }

    void MarkComplete();
}

public class RegulatoryNumberBackfillStatus : IRegulatoryNumberBackfillStatus
{
    private volatile bool _isComplete;

    public bool IsComplete => _isComplete;

    public void MarkComplete() => _isComplete = true;
}
