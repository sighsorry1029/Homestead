using UnityEngine;

namespace Homestead;

internal sealed class ZoneHammerRefreshScheduler
{
    private readonly float _delaySeconds;
    private readonly float _bulkDelaySeconds;
    private readonly float _minIntervalSeconds;
    private bool _pending;
    private bool _urgentPending;
    private float _refreshAt;
    private float _lastRefreshAt = -999f;
    private string _highlightName = "";

    public ZoneHammerRefreshScheduler(float delaySeconds, float bulkDelaySeconds, float minIntervalSeconds)
    {
        _delaySeconds = delaySeconds;
        _bulkDelaySeconds = bulkDelaySeconds;
        _minIntervalSeconds = minIntervalSeconds;
    }

    public void Reset()
    {
        _pending = false;
        _urgentPending = false;
        _refreshAt = 0f;
        _lastRefreshAt = -999f;
        _highlightName = "";
    }

    public void ClearPending()
    {
        _pending = false;
        _urgentPending = false;
        _refreshAt = 0f;
        _highlightName = "";
    }

    public void Request(string? highlightName, bool deferForBatch)
    {
        if (!string.IsNullOrWhiteSpace(highlightName))
        {
            _highlightName = highlightName!;
        }

        float now = Time.realtimeSinceStartup;
        float delay = deferForBatch ? _bulkDelaySeconds : _delaySeconds;
        float earliest = Mathf.Max(now + delay, _lastRefreshAt + _minIntervalSeconds);
        if (!_pending)
        {
            _refreshAt = earliest;
            _urgentPending = !deferForBatch;
        }
        else if (!deferForBatch)
        {
            _refreshAt = Mathf.Min(_refreshAt, earliest);
            _urgentPending = true;
        }
        else if (!_urgentPending)
        {
            _refreshAt = Mathf.Max(_refreshAt, earliest);
        }

        _pending = true;
    }

    public bool TryConsumeDue(out string highlightName)
    {
        highlightName = "";
        if (!_pending || Time.realtimeSinceStartup < _refreshAt)
        {
            return false;
        }

        highlightName = _highlightName;
        ClearPending();
        return true;
    }

    public void MarkCompleted()
    {
        _lastRefreshAt = Time.realtimeSinceStartup;
    }
}
