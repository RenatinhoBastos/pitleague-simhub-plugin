using System;

namespace PitLeague.SimHub.Adapters
{
    /// <summary>
    /// Interface for game-specific telemetry adapters.
    /// Each adapter knows how to capture race data from one game/source.
    /// Priority: specific adapters first (F1_25 UDP), GenericSimHub as fallback.
    /// </summary>
    public interface IGameTelemetryAdapter : IDisposable
    {
        /// <summary>Short id: "f125", "generic", "irsdk", "acc", etc.</summary>
        string AdapterId { get; }

        /// <summary>Schema version this adapter produces (always "pitleague-2.1").</summary>
        string SchemaVersion { get; }

        /// <summary>
        /// Check if this adapter can operate (e.g., UDP port available, shared memory accessible).
        /// For GenericSimHub: always true (fallback).
        /// </summary>
        bool IsAvailable();

        /// <summary>Start listeners/buffers. Idempotent.</summary>
        void Start();

        /// <summary>Stop listeners and release resources.</summary>
        void Stop();

        /// <summary>True if adapter received final classification (or equivalent) and has enough data.</summary>
        bool HasFinalClassification { get; }

        /// <summary>Consolidated snapshot of the race. Throws if HasFinalClassification is false.</summary>
        RaceTelemetrySnapshot GetSnapshot();

        /// <summary>Reset state for a new race session.</summary>
        void Reset();

        /// <summary>Available capabilities — used to populate _pitleague.richDataAvailable.</summary>
        string[] RichDataAvailable { get; }
    }
}
