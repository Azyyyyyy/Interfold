$path = "C:\Users\apearson\source\personal\octocon\csharp\Interfold.Domain\Fronting\EndFrontCommandHandler.cs"
$content = Get-Content $path -Raw

# Extract class name and result type
$classNameMatch = [regex]::Match($content, "public sealed class (\w+)")
$className = $classNameMatch.Groups[1].Value
$resultTypeMatch = [regex]::Match($content, "ICommandHandler<\w+,\s*(\w+)>")
$resultType = $resultTypeMatch.Groups[1].Value

# 1. Update inheritance
$content = $content -replace "ICommandHandler<([^,]+),\s*([^>]+)>", "IdempotentCommandHandler<`$1, `$2>"

# 2. Remove IIdempotencyStore field
$content = $content -replace "\s*private readonly IIdempotencyStore _idempotencyStore;\r?\n", "`n"

# 3. Update constructor
# Find the constructor definition and append : base(idempotencyStore)
$content = $content -replace "(public $className\s*\([^\)]*\)\s*)\r?\n\s*\{", "`$1 : base(idempotencyStore)`n    {"
$content = $content -replace "\s*_idempotencyStore = idempotencyStore;\r?\n", "`n"

# 4. HandleAsync -> ExecuteCoreAsync
$content = $content -replace "public\s+async\s+Task<CommandExecutionResult<([^>]+)>>\s+HandleAsync\(\s*CommandEnvelope<([^>]+)>\s+command,\s*CancellationToken\s+cancellationToken\s*=\s*default\s*\)", "protected override async Task<CommandExecutionResult<`$1>> ExecuteCoreAsync(`n        CommandEnvelope<`$2> command,`n        CancellationToken cancellationToken)"

# 5. Extract DuplicateEntityRef and insert overrides
if ($content -match "RejectDuplicate\(command,\s*([^\)]+)\)") {
    $entityRef = $matches[1]
    
    $overrides = @"

    protected override EntityRef DuplicateEntityRef => $entityRef;

    protected override $resultType CreateReplayResult($resultType originalResult) =>
        originalResult with { Replay = true };
"@

    # Insert after constructor
    $content = $content -replace "(\s*public $className\([^\)]*\)[^\{]*\{[^\}]*\})", "`$1$overrides"
}

# 6. Remove RejectDuplicate method
$content = $content -replace "(?s)\s*private static CommandExecutionResult<[^>]+> RejectDuplicate[^\}]+?ResolutionHint\.NoRetry[^\}]+?\}[^\}]*?\}", "`n"

# 7. Remove Idempotency boilerplate from ExecuteCoreAsync
$content = $content -replace "(?s)\s*var payloadJson = CommandSerialization.Serialize\(command.Payload\);.*?(?=\s*var[^\n]+await _)", ""
$content = $content -replace "(?s)\s*var resultJson = CommandSerialization.Serialize\(result\);\s*await _idempotencyStore\.SaveAsync\(.*?cancellationToken\s*\);", ""

# Write back
Set-Content -Path $path -Value $content
