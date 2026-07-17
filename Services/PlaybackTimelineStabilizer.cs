namespace Mystral.Services;

internal sealed class PlaybackTimelineStabilizer
{
    private static readonly TimeSpan BackwardReconciliationTolerance = TimeSpan.FromMilliseconds(1250);
    private static readonly TimeSpan BackwardMovementMinimum = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SeekConfirmationTolerance = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SeekConfirmationGuard = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan SeekProtectionWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SeekMovementBias = TimeSpan.FromMilliseconds(125);

    private string _mediaKey = string.Empty;
    private TimeSpan _anchorPosition;
    private TimeSpan _duration;
    private DateTimeOffset _anchorAt;
    private bool _isPlaying;
    private bool _hasAnchor;
    private TimelineObservation? _latestObservation;
    private PendingSeek? _pendingSeek;
    private TimelineObservation? _backwardCandidate;

    public TimeSpan Duration => _hasAnchor ? _duration : TimeSpan.Zero;

    public TimeSpan Observe(
        string mediaKey,
        bool hasSession,
        TimeSpan position,
        TimeSpan duration,
        bool isPlaying,
        DateTimeOffset timelineUpdatedAt,
        bool hasReliableTimelineUpdatedAt,
        DateTimeOffset observedAt)
    {
        if (!hasSession || string.IsNullOrWhiteSpace(mediaKey))
        {
            Reset();
            return TimeSpan.Zero;
        }

        if (duration <= TimeSpan.Zero)
        {
            if (!_hasAnchor || !string.Equals(_mediaKey, mediaKey, StringComparison.Ordinal))
            {
                Reset();
                return TimeSpan.Zero;
            }

            var preservedPosition = GetPosition(observedAt);
            SetAnchor(mediaKey, preservedPosition, _duration, isPlaying, observedAt);
            return preservedPosition;
        }

        var rawPosition = Clamp(position, duration);
        position = ProjectSourcePosition(
            rawPosition,
            duration,
            isPlaying,
            timelineUpdatedAt,
            observedAt);
        var observation = new TimelineObservation(
            mediaKey,
            rawPosition,
            position,
            duration,
            isPlaying,
            timelineUpdatedAt,
            hasReliableTimelineUpdatedAt,
            observedAt);
        _latestObservation = observation;

        if (!_hasAnchor || !string.Equals(_mediaKey, mediaKey, StringComparison.Ordinal))
        {
            _pendingSeek = null;
            _backwardCandidate = null;
            SetAnchor(mediaKey, position, duration, isPlaying, observedAt);
            return position;
        }

        var displayedPosition = GetPosition(observedAt);
        if (_pendingSeek is { } pendingSeek)
        {
            var seekAge = observedAt - pendingSeek.StartedAt;
            var originalPosition = Advance(
                pendingSeek.OriginalPosition,
                pendingSeek.OriginalWasPlaying,
                seekAge,
                duration);
            var targetDistance = Distance(position, displayedPosition);
            var originalDistance = Distance(position, originalPosition);
            var seekDistance = Distance(pendingSeek.TargetPosition, pendingSeek.OriginalPosition);
            var movedTowardTarget = seekDistance <= SeekMovementBias
                || targetDistance + SeekMovementBias < originalDistance;

            if (seekAge >= SeekConfirmationGuard
                && targetDistance <= SeekConfirmationTolerance
                && movedTowardTarget)
            {
                _pendingSeek = null;
                _backwardCandidate = null;
            }
            else if (seekAge <= SeekProtectionWindow)
            {
                SetAnchor(mediaKey, displayedPosition, duration, isPlaying, observedAt);
                return displayedPosition;
            }
            else
            {
                _pendingSeek = null;
            }
        }

        if (position < displayedPosition && (_isPlaying || isPlaying))
        {
            if (_backwardCandidate is not { } candidate
                || !IsCoherentBackwardObservation(candidate, observation))
            {
                _backwardCandidate = observation;
                SetAnchor(mediaKey, displayedPosition, duration, isPlaying, observedAt);
                return displayedPosition;
            }

            _backwardCandidate = null;
        }
        else
        {
            _backwardCandidate = null;
        }

        SetAnchor(mediaKey, position, duration, isPlaying, observedAt);
        return position;
    }

    public TimeSpan BeginSeek(
        string mediaKey,
        TimeSpan targetPosition,
        TimeSpan duration,
        bool isPlaying,
        DateTimeOffset startedAt)
    {
        if (string.IsNullOrWhiteSpace(mediaKey) || duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var originalPosition = _hasAnchor && string.Equals(_mediaKey, mediaKey, StringComparison.Ordinal)
            ? GetPosition(startedAt)
            : TimeSpan.Zero;
        var originalWasPlaying = _hasAnchor && _isPlaying;
        targetPosition = Clamp(targetPosition, duration);

        SetAnchor(mediaKey, targetPosition, duration, isPlaying, startedAt);
        _backwardCandidate = null;
        _pendingSeek = new PendingSeek(
            TargetPosition: targetPosition,
            OriginalPosition: originalPosition,
            OriginalWasPlaying: originalWasPlaying,
            StartedAt: startedAt);
        return targetPosition;
    }

    public void RejectPendingSeek(DateTimeOffset rejectedAt)
    {
        if (_pendingSeek is null)
        {
            return;
        }

        _pendingSeek = null;
        _backwardCandidate = null;
        if (_latestObservation is not { } latest
            || !string.Equals(latest.MediaKey, _mediaKey, StringComparison.Ordinal))
        {
            return;
        }

        var elapsed = rejectedAt - latest.ObservedAt;
        var restoredPosition = Advance(latest.Position, latest.IsPlaying, elapsed, latest.Duration);
        SetAnchor(latest.MediaKey, restoredPosition, latest.Duration, latest.IsPlaying, rejectedAt);
    }

    public TimeSpan GetPosition(DateTimeOffset now)
    {
        if (!_hasAnchor)
        {
            return TimeSpan.Zero;
        }

        return Advance(_anchorPosition, _isPlaying, now - _anchorAt, _duration);
    }

    public static TimeSpan ProjectSourcePosition(
        TimeSpan position,
        TimeSpan duration,
        bool isPlaying,
        DateTimeOffset sourceUpdatedAt,
        DateTimeOffset observedAt)
    {
        position = Clamp(position, duration);
        if (!isPlaying)
        {
            return position;
        }

        var anchorAt = ResolveSourceAnchorTimestamp(
            sourceUpdatedAt,
            observedAt,
            duration,
            out _);
        var elapsed = observedAt - anchorAt;
        if (elapsed <= TimeSpan.Zero)
        {
            return position;
        }

        return Clamp(position + elapsed, duration);
    }

    public static DateTimeOffset ResolveSourceAnchorTimestamp(
        DateTimeOffset sourceUpdatedAt,
        DateTimeOffset observedAt,
        TimeSpan duration,
        out bool isReliable)
    {
        isReliable = false;
        if (sourceUpdatedAt == default)
        {
            return observedAt;
        }

        var age = observedAt - sourceUpdatedAt;
        var maximumCredibleAge = duration > TimeSpan.Zero
            ? duration + TimeSpan.FromMinutes(1)
            : TimeSpan.FromHours(12);
        if (age < TimeSpan.Zero || age > maximumCredibleAge)
        {
            return observedAt;
        }

        isReliable = true;
        return sourceUpdatedAt;
    }

    private void SetAnchor(
        string mediaKey,
        TimeSpan position,
        TimeSpan duration,
        bool isPlaying,
        DateTimeOffset anchorAt)
    {
        _mediaKey = mediaKey;
        _anchorPosition = Clamp(position, duration);
        _duration = duration;
        _isPlaying = isPlaying;
        _anchorAt = anchorAt;
        _hasAnchor = true;
    }

    private void Reset()
    {
        _mediaKey = string.Empty;
        _anchorPosition = TimeSpan.Zero;
        _duration = TimeSpan.Zero;
        _anchorAt = default;
        _isPlaying = false;
        _hasAnchor = false;
        _latestObservation = null;
        _pendingSeek = null;
        _backwardCandidate = null;
    }

    private static TimeSpan Advance(
        TimeSpan position,
        bool isPlaying,
        TimeSpan elapsed,
        TimeSpan duration)
    {
        if (isPlaying && elapsed > TimeSpan.Zero)
        {
            position += elapsed;
        }

        return Clamp(position, duration);
    }

    private static TimeSpan Clamp(TimeSpan position, TimeSpan duration)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return duration > TimeSpan.Zero && position > duration ? duration : position;
    }

    private static TimeSpan Distance(TimeSpan left, TimeSpan right)
    {
        return (left - right).Duration();
    }

    private static bool IsCoherentBackwardObservation(
        TimelineObservation candidate,
        TimelineObservation observation)
    {
        if (!candidate.HasReliableTimelineUpdatedAt
            || !observation.HasReliableTimelineUpdatedAt
            || observation.TimelineUpdatedAt < candidate.TimelineUpdatedAt)
        {
            return false;
        }

        var elapsed = observation.ObservedAt - candidate.ObservedAt;
        if (elapsed <= TimeSpan.Zero)
        {
            return false;
        }

        var expected = Advance(
            candidate.Position,
            candidate.IsPlaying,
            elapsed,
            observation.Duration);
        if (Distance(observation.Position, expected) > BackwardReconciliationTolerance)
        {
            return false;
        }

        var progressed = observation.Position - candidate.Position >= BackwardMovementMinimum;
        var sourceAdvanced = observation.TimelineUpdatedAt > candidate.TimelineUpdatedAt;
        var pausedConfirmation = !candidate.IsPlaying
            && !observation.IsPlaying
            && (sourceAdvanced
                || Distance(observation.RawPosition, candidate.RawPosition)
                    <= BackwardReconciliationTolerance);
        return progressed || pausedConfirmation;
    }

    private sealed record TimelineObservation(
        string MediaKey,
        TimeSpan RawPosition,
        TimeSpan Position,
        TimeSpan Duration,
        bool IsPlaying,
        DateTimeOffset TimelineUpdatedAt,
        bool HasReliableTimelineUpdatedAt,
        DateTimeOffset ObservedAt);

    private sealed record PendingSeek(
        TimeSpan TargetPosition,
        TimeSpan OriginalPosition,
        bool OriginalWasPlaying,
        DateTimeOffset StartedAt);
}
