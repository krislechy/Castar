#Requires -Version 5.1
#Requires -PSEdition Desktop

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$localDb = (Get-Command SqlLocalDB.exe -CommandType Application).Source
$tar = (Get-Command tar.exe -CommandType Application).Source
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if (Test-Path -LiteralPath $output) {
    throw 'Choose a new export filename.'
}

$entries = @(& $tar -tf $archive)
if ($LASTEXITCODE -ne 0 -or $entries.Count -ne 2 -or
    'origin_data_base.mdf' -cnotin $entries -or 'origin_data_base_log.ldf' -cnotin $entries) {
    throw 'The archive must contain the original February 2022 MDF and LDF pair.'
}

$instance = 'CastarExport' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$temporary = Join-Path ([IO.Path]::GetTempPath()) $instance
$pending = $output + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
$mdf = Join-Path $temporary 'origin_data_base.mdf'
$ldf = Join-Path $temporary 'origin_data_base_log.ldf'
$instanceCreated = $false

function Open-Database([string]$Database) {
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder['Data Source'] = "(LocalDB)\$instance"
    $builder['Initial Catalog'] = $Database
    $builder['Integrated Security'] = $true
    $builder['Pooling'] = $false
    $builder['Connect Timeout'] = 15
    $connection = New-Object System.Data.SqlClient.SqlConnection $builder.get_ConnectionString()
    try {
        $connection.Open()
        return $connection
    } catch {
        $connection.Dispose()
        throw
    }
}

function Read-Queries([System.Data.SqlClient.SqlConnection]$Connection, [string]$Sql) {
    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = $Sql
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $row = [ordered]@{}
                for ($column = 0; $column -lt $reader.FieldCount; $column++) {
                    $row[$reader.GetName($column)] = if ($reader.IsDBNull($column)) {
                        $null
                    } else {
                        $reader.GetValue($column)
                    }
                }
                [pscustomobject]$row
            }
        } finally {
            $reader.Dispose()
        }
    } finally {
        $command.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Path $temporary | Out-Null
    & $tar -xf $archive -C $temporary
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the legacy archive.' }
    foreach ($path in @($mdf, $ldf)) {
        $file = Get-Item -LiteralPath $path
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'The archive database files must be ordinary files.'
        }
    }

    & $localDb create $instance | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create a temporary LocalDB instance.' }
    $instanceCreated = $true
    & $localDb start $instance | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the temporary LocalDB instance.' }

    $master = Open-Database 'master'
    try {
        $command = $master.CreateCommand()
        try {
            $mdfPath = $mdf.Replace("'", "''")
            $ldfPath = $ldf.Replace("'", "''")
            $command.CommandText = "CREATE DATABASE [CastarLegacyArchive] ON (FILENAME=N'$mdfPath'), (FILENAME=N'$ldfPath') FOR ATTACH;"
            $command.CommandTimeout = 120
            $command.ExecuteNonQuery() | Out-Null
        } finally {
            $command.Dispose()
        }
    } finally {
        $master.Dispose()
    }

    $database = Open-Database 'CastarLegacyArchive'
    try {
        $incomes = @(Read-Queries $database @'
SELECT Id AS id, who AS telegramId, CONVERT(varchar(33), dt, 126) AS addedOn,
    CONVERT(decimal(28,10), [Sum]) AS amount, comment AS source
FROM dbo.incomes ORDER BY Id;
'@)
        $purchases = @(Read-Queries $database @'
SELECT Id AS id, WhoPaid AS telegramId, CONVERT(varchar(33), dt, 126) AS addedOn,
    CONVERT(decimal(28,10), [Sum]) AS amount, Name AS name
FROM dbo.purchases ORDER BY Id;
'@)
        $items = @(Read-Queries $database @'
SELECT Id AS id, PurchaseId AS purchaseId, Name AS name, Quantity AS quantity,
    CONVERT(decimal(28,10), Price) AS price, CONVERT(decimal(28,10), [Sum]) AS amount, Category AS category
FROM dbo.items ORDER BY Id;
'@)
        $history = Read-Queries $database 'SELECT COUNT(*) AS [rowCount] FROM dbo.historyoftransactions;'
    } finally {
        $database.Dispose()
    }

    $payload = [ordered]@{
        format = 1
        archiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        historyTransactionCount = $history.rowCount
        incomes = $incomes
        purchases = $purchases
        items = $items
    }
    $json = ConvertTo-Json -InputObject $payload -Depth 6 -Compress
    [IO.File]::WriteAllText($pending, $json, (New-Object System.Text.UTF8Encoding $false))
    [IO.File]::Move($pending, $output)
    Write-Output "Exported $($incomes.Count) incomes, $($purchases.Count) purchases and $($items.Count) items."
} finally {
    if ($instanceCreated) {
        & $localDb stop $instance | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Could not stop the temporary LocalDB instance $instance."
        } else {
            & $localDb delete $instance | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Warning "Could not remove the temporary LocalDB instance $instance." }
        }
    }
    foreach ($path in @($mdf, $ldf, $pending)) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path -Force -ErrorAction Continue
        }
    }
    if (Test-Path -LiteralPath $temporary -PathType Container) {
        [IO.Directory]::Delete($temporary)
    }
}
