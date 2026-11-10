using System;

namespace AdimiDuel_Server.Duel;

// Deliberately small, deterministic spacing assistance. This is a hand-written
// policy, not a model trained on player data. Native still owns combat input.
internal sealed class AdimiToolsAiArenaFootworkPolicy
{
    private const float MinimumDistance = 0.4f;
    private const double UnsafeRetrySeconds = 0.45d;

    private int _botIndex = -1;
    private int _targetIndex = -1;
    private int _difficulty;
    private int _nextSide;
    private int _currentSide;
    private double _lastTime = -1d;
    private double _stepUntil;
    private double _cooldownUntil;
    private double _unsafeRetryUntil;
    private double _stepSeconds;
    private double _pauseSeconds;
    private double _evadeUntil;
    private double _kickSeenAt = -1d;
    private double _kickReactionDelay;
    private float _lateralMagnitude;
    private float _retreatMagnitude;
    private float _approachMagnitude;
    private float _evadeRetreatMagnitude;
    private float _evadeLateralMagnitude;
    private bool _stepping;
    private bool _kickResponded;

    // Both outputs are relative to the direction from the bot to its target:
    // positive toward approaches the target; positive lateral is to the right.
    internal bool TryGetStep(double now, int botIndex, int targetIndex, float distance, float weaponLength,
        out float toward, out float lateral, bool incomingKick = false, int difficulty = 3)
    {
        toward = 0f;
        lateral = 0f;

        if (!double.IsFinite(now) || now < 0d || botIndex < 0 || targetIndex < 0 || botIndex == targetIndex)
        {
            Reset();
            return false;
        }

        // Unknown difficulty fails to the least assisted profile. The mission
        // supplies its selected difficulty; the default preserves the prior
        // behavior for callers that explicitly use this policy in isolation.
        int level = difficulty is >= 1 and <= 3 ? difficulty : 1;
        if (_botIndex != botIndex || _targetIndex != targetIndex || _difficulty != level || now < _lastTime)
        {
            // Switching targets or difficulty cannot turn an unsafe-path
            // rejection into immediate movement. A new bot or clock resets it.
            double unsafeRetryUntil = _botIndex == botIndex && now >= _lastTime ? _unsafeRetryUntil : 0d;
            bool alreadyResponded = _botIndex == botIndex && _targetIndex == targetIndex
                && now >= _lastTime && incomingKick && _kickResponded;
            Reset();
            Initialize(botIndex, targetIndex, level);
            _unsafeRetryUntil = unsafeRetryUntil;
            _kickResponded = alreadyResponded;
        }

        _lastTime = now;

        // Leave unusually short weapons, malformed observations, overlaps and
        // opponents outside useful melee range to the native controller.
        if (!float.IsFinite(distance) || !float.IsFinite(weaponLength) || weaponLength < 0.2f
            || distance < MinimumDistance || distance > Math.Clamp(weaponLength + 1.1f, 2.1f, 3.3f))
        {
            _kickSeenAt = -1d;
            if (!incomingKick) _kickResponded = false;
            EndStep(now);
            return false;
        }

        // Track continuous kicks only while the opponent is close enough.
        // A kick that ends or moves away before the reaction delay cannot
        // trigger a stale dodge later; re-entry starts a fresh observation.
        bool kickInRange = incomingKick && distance <= 1.8f;
        if (!incomingKick)
        {
            _kickSeenAt = -1d;
            _kickResponded = false;
        }
        else if (!kickInRange)
        {
            _kickSeenAt = -1d;
        }
        else if (!_kickResponded && _kickSeenAt < 0d)
        {
            _kickSeenAt = now;
        }

        // A kick can interrupt an ordinary pause, but must never bypass a
        // rejected path's safety cooldown.
        if (now < _unsafeRetryUntil) return false;

        if (kickInRange && !_kickResponded && now - _kickSeenAt >= _kickReactionDelay)
        {
            // React once per observed kick, even during the ordinary pause.
            // Native still resolves the kick, attack and any hit reaction.
            _kickResponded = true;
            if (!_stepping)
            {
                _currentSide = _nextSide;
                _nextSide = -_nextSide;
            }

            _stepping = true;
            _evadeUntil = now + 0.25d;
            _stepUntil = _evadeUntil;
        }

        if (_stepping && now >= _stepUntil)
        {
            _stepping = false;
            _cooldownUntil = Math.Max(_cooldownUntil, _stepUntil + _pauseSeconds);
        }

        if (!_stepping)
        {
            if (now < _cooldownUntil)
            {
                return false;
            }

            _stepping = true;
            _currentSide = _nextSide;
            _nextSide = -_nextSide;
            _stepUntil = now + _stepSeconds;
        }

        // Estimate centre-to-centre reach from weapon length and a conservative
        // arm allowance. Actual reach depends on animation, body and opponent;
        // this is only a spacing preference, not a hit guarantee. The wide
        // neutral band and gradual radial ramp avoid frame-by-frame reversals.
        // Short weapons must still close in rather than stay beyond their reach.
        float lowerDistance = Math.Clamp(weaponLength + 0.20f, 0.80f, 2.35f);
        float upperDistance = Math.Clamp(weaponLength + 0.45f, 1.05f, 2.60f);
        if (now < _evadeUntil)
        {
            toward = -_evadeRetreatMagnitude;
            lateral = _currentSide * _evadeLateralMagnitude;
            return true;
        }

        if (distance < lowerDistance)
        {
            toward = -_retreatMagnitude * Math.Clamp((lowerDistance - distance) / 0.35f, 0f, 1f);
        }
        else if (distance > upperDistance)
        {
            toward = _approachMagnitude * Math.Clamp((distance - upperDistance) / 0.45f, 0f, 1f);
        }

        lateral = _currentSide * _lateralMagnitude;
        return true;
    }

    // The caller uses this when a candidate path or landing point is unsafe.
    // Do not immediately switch sides: that would oscillate at a wall.
    internal void Reject(double now)
    {
        if (!double.IsFinite(now) || now < 0d || (_lastTime >= 0d && now < _lastTime))
        {
            Reset();
            return;
        }

        _lastTime = now;
        _stepping = false;
        _evadeUntil = 0d;
        _unsafeRetryUntil = Math.Max(_unsafeRetryUntil, now + UnsafeRetrySeconds);
        _cooldownUntil = Math.Max(_cooldownUntil, Math.Max(_unsafeRetryUntil, now + _pauseSeconds));
    }

    internal void Reset()
    {
        _botIndex = -1;
        _targetIndex = -1;
        _difficulty = 0;
        _nextSide = 0;
        _currentSide = 0;
        _lastTime = -1d;
        _stepUntil = 0d;
        _cooldownUntil = 0d;
        _unsafeRetryUntil = 0d;
        _stepSeconds = 0d;
        _pauseSeconds = 0d;
        _evadeUntil = 0d;
        _kickSeenAt = -1d;
        _kickReactionDelay = 0d;
        _lateralMagnitude = 0f;
        _retreatMagnitude = 0f;
        _approachMagnitude = 0f;
        _evadeRetreatMagnitude = 0f;
        _evadeLateralMagnitude = 0f;
        _stepping = false;
        _kickResponded = false;
    }

    private void Initialize(int botIndex, int targetIndex, int difficulty)
    {
        _botIndex = botIndex;
        _targetIndex = targetIndex;
        _difficulty = difficulty;
        uint seed = unchecked(((uint)botIndex * 2654435761u) ^ ((uint)targetIndex * 2246822519u));
        _nextSide = (seed & 1u) == 0u ? 1 : -1;
        switch (difficulty)
        {
            case 1:
                _stepSeconds = 0.24d + (seed % 3u) * 0.02d;
                _pauseSeconds = 0.65d + ((seed >> 3) % 5u) * 0.03d;
                _kickReactionDelay = 0.20d;
                _lateralMagnitude = 0.58f;
                _retreatMagnitude = 0.35f;
                _approachMagnitude = 0.22f;
                _evadeRetreatMagnitude = 0.40f;
                _evadeLateralMagnitude = 0.40f;
                break;
            case 2:
                _stepSeconds = 0.30d + (seed % 4u) * 0.02d;
                _pauseSeconds = 0.40d + ((seed >> 3) % 5u) * 0.03d;
                _kickReactionDelay = 0.10d;
                _lateralMagnitude = 0.70f;
                _retreatMagnitude = 0.45f;
                _approachMagnitude = 0.28f;
                _evadeRetreatMagnitude = 0.55f;
                _evadeLateralMagnitude = 0.53f;
                break;
            default:
                _stepSeconds = 0.36d + (seed % 5u) * 0.02d;
                _pauseSeconds = 0.22d + ((seed >> 3) % 5u) * 0.03d;
                _kickReactionDelay = 0d;
                _lateralMagnitude = 0.82f;
                _retreatMagnitude = 0.55f;
                _approachMagnitude = 0.35f;
                _evadeRetreatMagnitude = 0.70f;
                _evadeLateralMagnitude = 0.65f;
                break;
        }
    }

    private void EndStep(double now)
    {
        if (_stepping)
        {
            _stepping = false;
            _evadeUntil = 0d;
            _cooldownUntil = Math.Max(_cooldownUntil, now + _pauseSeconds);
        }
    }
}
