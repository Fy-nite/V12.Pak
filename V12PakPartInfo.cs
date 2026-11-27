namespace V12.Pak;

/// <summary>
/// Size and SHA-256 of one split-pak part, recorded in <c>game.json</c>.
///
/// The point is delivery: a resumable downloader can skip a part it already has,
/// and re-fetch only the one that failed verification. The loader does not need
/// this to read a local build, so verification is opt-in.
/// </summary>
public sealed record V12PakPartInfo(long Bytes, string Sha256);
