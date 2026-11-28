#Requires -Version 7.0
# Dot-sourced by the package script tests. Exposes the AST and its selected statement ranges.
$scriptsDirectory = Join-Path $PSScriptRoot '../../scripts'
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
. (Join-Path $scriptsDirectory 'package-version.ps1')
$source = [IO.File]::ReadAllText((Join-Path $scriptsDirectory 'package.ps1'), [Text.Encoding]::UTF8)
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Cannot parse package.ps1' }
# Tests select statements in execution order, including the guarded output-producing block.
$statements = @(foreach ($statement in $ast.EndBlock.Statements) {
    $guard = $statement -is [Management.Automation.Language.PipelineAst] -and
        $statement.PipelineElements[0] -is [Management.Automation.Language.CommandAst] -and
        $statement.PipelineElements[0].GetCommandName() -eq 'Invoke-WithPackageOutputLock'
    if ($guard) {
        $statement.PipelineElements[0].CommandElements[-1].ScriptBlock.EndBlock.Statements
    } else { $statement }
})

function Get-StatementRange([string]$From, [string]$Until, [switch]$Inclusive) {
    # Top-level statements from the first whose text matches $From up to the first later one
    # matching $Until (excluded unless -Inclusive).
    $start = -1
    for ($i = 0; $i -lt $statements.Count; $i++) {
        if ($statements[$i].Extent.Text -match $From) { $start = $i; break }
    }
    if ($start -lt 0) { throw "No statement matches $From" }
    for ($j = $start + 1; $j -lt $statements.Count; $j++) {
        if ($statements[$j].Extent.Text -match $Until) {
            $end = if ($Inclusive) { $j } else { $j - 1 }
            return ($statements[$start..$end] | ForEach-Object { $_.Extent.Text }) -join "`n"
        }
    }
    throw "No statement after $From matches $Until"
}

function Get-Assignments([string[]]$Names) {
    # Top-level assignments to the named variables, in script order.
    return (@($statements | Where-Object {
        $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
        $_.Left.Extent.Text -in $Names
    }) | ForEach-Object { $_.Extent.Text }) -join "`n"
}

function Get-FunctionDefinitions([string[]]$Names) {
    return (@($statements | Where-Object {
        $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -in $Names
    }) | ForEach-Object { $_.Extent.Text }) -join "`n"
}
