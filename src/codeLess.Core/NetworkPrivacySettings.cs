namespace codeLess.Studio
{
    /// <summary>Persisted privacy settings (shared by Windows and macOS/Linux; C# 5).</summary>
    public partial class IdePersistentSettings
    {
        /// <summary>
        /// Offline Mode: refuse every non-loopback request codeLess would make (cloud AI, update check, OpenVSX).
        /// Local Ollama / LM Studio / custom loopback endpoints keep working. Applied through NetworkLedger.SetOfflineMode.
        /// </summary>
        public bool OfflineMode { get; set; }
    }
}
