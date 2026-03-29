using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lab6;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static int Main(string[] args)
    {
        try
        {
            return args.Length switch
            {
                0 => RunMenu(),
                _ when args[0].Equals("party", StringComparison.OrdinalIgnoreCase) => RunParty(args),
                _ when args[0].Equals("coordinator", StringComparison.OrdinalIgnoreCase) => RunCoordinatorFromArgs(args),
                _ when args[0].Equals("showlogs", StringComparison.OrdinalIgnoreCase) => ShowLastRunLogs(),
                _ => ShowUnknownCommand(args)
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
            Console.WriteLine(ex);
            return 1;
        }
    }

    private static int RunMenu()
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("=== Lab 6. Atomic Swap ===");
            Console.WriteLine("1) Run successful three-party atomic swap demo");
            Console.WriteLine("2) Run timeout/refund demo");
            Console.WriteLine("3) Show final logs");
            Console.WriteLine("4) Exit");
            Console.Write("Select option: ");

            var choice = Console.ReadLine()?.Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1":
                    RunCoordinator(ScenarioKind.Success);
                    break;
                case "2":
                    RunCoordinator(ScenarioKind.Timeout);
                    break;
                case "3":
                    ShowLastRunLogs();
                    break;
                case "4":
                    return 0;
                default:
                    Console.WriteLine("Unknown option. Please select 1, 2, 3 or 4.");
                    break;
            }
        }
    }

    private static int RunCoordinatorFromArgs(string[] args)
    {
        if (args.Length < 2 || !Enum.TryParse<ScenarioKind>(args[1], true, out var kind))
        {
            Console.WriteLine("Usage: coordinator <Success|Timeout>");
            return 1;
        }

        RunCoordinator(kind);
        return 0;
    }

    private static int RunParty(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: party <A|B|C> <scenarioRoot>");
            return 1;
        }

        var partyName = $"Party{args[1].Trim().ToUpperInvariant()}";
        var scenarioRoot = args[2];
        var context = ScenarioContext.Load(scenarioRoot);
        var logger = new FileLogger(context.GetLogPath(partyName), partyName);

        logger.Info($"Process started for {partyName}.");

        try
        {
            switch (partyName)
            {
                case "PartyA":
                    PartyProcess.RunPartyA(context, logger);
                    break;
                case "PartyB":
                    PartyProcess.RunPartyB(context, logger);
                    break;
                case "PartyC":
                    PartyProcess.RunPartyC(context, logger);
                    break;
                default:
                    logger.Error($"Unknown party name: {partyName}");
                    return 1;
            }

            logger.Info("Process completed.");
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error($"Process failed: {ex.Message}");
            logger.Error(ex.ToString());
            return 1;
        }
    }

    private static int ShowUnknownCommand(string[] args)
    {
        Console.WriteLine($"Unknown command: {string.Join(' ', args)}");
        Console.WriteLine("Supported commands: party, coordinator, showlogs");
        return 1;
    }

    private static void RunCoordinator(ScenarioKind kind)
    {
        var scenarioRoot = ScenarioContext.Create(kind);
        var context = ScenarioContext.Load(scenarioRoot);
        var logger = new FileLogger(context.GetLogPath("Coordinator"), "Coordinator");

        logger.Info($"Coordinator started scenario: {kind}");
        logger.Info($"Scenario root: {scenarioRoot}");

        Console.WriteLine($"Running scenario: {kind}");
        Console.WriteLine($"Scenario folder: {scenarioRoot}");

        var processes = new List<Process>
        {
            StartPartyProcess("A", context),
            StartPartyProcess("B", context),
            StartPartyProcess("C", context)
        };

        foreach (var process in processes)
        {
            process.WaitForExit();
            logger.Info($"Child process PID={process.Id} finished with exit code {process.ExitCode}.");
        }

        var balances = context.LoadBalances();
        var contracts = context.LoadContracts();

        logger.Info("Final contract states:");
        foreach (var contract in contracts.OrderBy(c => c.ContractId))
        {
            logger.Info($"{contract.ContractId}: {contract.Status}");
        }

        logger.Info("Final balances:");
        foreach (var party in balances.OrderBy(x => x.Key))
        {
            logger.Info($"{party.Key}: {FormatAssets(party.Value)}");
        }

        var summary = BuildSummary(kind, context, balances, contracts, processes);
        File.WriteAllText(context.SummaryPath, summary, Encoding.UTF8);

        Console.WriteLine(summary);
        Console.WriteLine($"Detailed logs are stored in: {context.LogsDirectory}");
    }

    private static Process StartPartyProcess(string partyLetter, ScenarioContext context)
    {
        var dllPath = Assembly.GetExecutingAssembly().Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = context.RepositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add(dllPath);
        startInfo.ArgumentList.Add("party");
        startInfo.ArgumentList.Add(partyLetter);
        startInfo.ArgumentList.Add(context.RootDirectory);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                Console.WriteLine(eventArgs.Data);
            }
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                Console.WriteLine(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start Party{partyLetter}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static string BuildSummary(
        ScenarioKind kind,
        ScenarioContext context,
        Dictionary<string, Dictionary<string, decimal>> balances,
        List<HtlcContract> contracts,
        List<Process> processes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Scenario Summary ===");
        sb.AppendLine($"Scenario: {kind}");
        sb.AppendLine($"Path: {context.RootDirectory}");
        sb.AppendLine("Processes:");

        foreach (var process in processes)
        {
            sb.AppendLine($"  PID {process.Id}, ExitCode={process.ExitCode}");
        }

        sb.AppendLine("Contracts:");
        foreach (var contract in contracts.OrderBy(c => c.ContractId))
        {
            sb.AppendLine(
                $"  {contract.ContractId}: {contract.FromParty} -> {contract.ToParty}, " +
                $"{contract.Amount} {contract.AssetName}, Status={contract.Status}");
        }

        sb.AppendLine("Balances:");
        foreach (var party in balances.OrderBy(x => x.Key))
        {
            sb.AppendLine($"  {party.Key}: {FormatAssets(party.Value)}");
        }

        return sb.ToString();
    }

    private static int ShowLastRunLogs()
    {
        var runsRoot = Path.Combine(Environment.CurrentDirectory, "runs");
        var lastRunFile = Path.Combine(runsRoot, "last_run.txt");

        if (!File.Exists(lastRunFile))
        {
            Console.WriteLine("No runs have been executed yet.");
            return 0;
        }

        var scenarioRoot = File.ReadAllText(lastRunFile).Trim();
        if (string.IsNullOrWhiteSpace(scenarioRoot) || !Directory.Exists(scenarioRoot))
        {
            Console.WriteLine("Last run path is invalid.");
            return 1;
        }

        var context = ScenarioContext.Load(scenarioRoot);

        Console.WriteLine($"Logs from: {scenarioRoot}");
        Console.WriteLine();

        if (File.Exists(context.SummaryPath))
        {
            Console.WriteLine(File.ReadAllText(context.SummaryPath));
        }

        foreach (var logPath in Directory.GetFiles(context.LogsDirectory, "*.log").OrderBy(x => x))
        {
            Console.WriteLine($"--- {Path.GetFileName(logPath)} ---");
            Console.WriteLine(File.ReadAllText(logPath));
        }

        return 0;
    }

    private static string FormatAssets(Dictionary<string, decimal> assets)
    {
        return string.Join(", ", assets.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));
    }

    private sealed class ScenarioContext
    {
        public required string RepositoryRoot { get; init; }
        public required string RootDirectory { get; init; }
        public required string StateDirectory { get; init; }
        public required string LogsDirectory { get; init; }
        public required string LockFilePath { get; init; }
        public required string ConfigPath { get; init; }
        public required string BalancesPath { get; init; }
        public required string SummaryPath { get; init; }
        public required ScenarioConfig Config { get; init; }

        public static string Create(ScenarioKind kind)
        {
            var repositoryRoot = Environment.CurrentDirectory;
            var runsRoot = Path.Combine(repositoryRoot, "runs");
            Directory.CreateDirectory(runsRoot);

            var scenarioRoot = Path.Combine(runsRoot, $"{kind.ToString().ToLowerInvariant()}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
            var stateDirectory = Path.Combine(scenarioRoot, "state");
            var logsDirectory = Path.Combine(scenarioRoot, "logs");

            Directory.CreateDirectory(scenarioRoot);
            Directory.CreateDirectory(stateDirectory);
            Directory.CreateDirectory(logsDirectory);

            var config = ScenarioConfig.Create(kind);
            var context = new ScenarioContext
            {
                RepositoryRoot = repositoryRoot,
                RootDirectory = scenarioRoot,
                StateDirectory = stateDirectory,
                LogsDirectory = logsDirectory,
                LockFilePath = Path.Combine(stateDirectory, "scenario.lock"),
                ConfigPath = Path.Combine(stateDirectory, "scenario.json"),
                BalancesPath = Path.Combine(stateDirectory, "balances.json"),
                SummaryPath = Path.Combine(scenarioRoot, "summary.txt"),
                Config = config
            };

            context.SaveConfig();
            context.SaveBalances(config.CreateInitialBalances());
            File.WriteAllText(Path.Combine(runsRoot, "last_run.txt"), scenarioRoot, Encoding.UTF8);

            return scenarioRoot;
        }

        public static ScenarioContext Load(string scenarioRoot)
        {
            var stateDirectory = Path.Combine(scenarioRoot, "state");
            var configPath = Path.Combine(stateDirectory, "scenario.json");
            var config = ReadJson<ScenarioConfig>(configPath);

            return new ScenarioContext
            {
                RepositoryRoot = Directory.GetParent(scenarioRoot)?.Parent?.FullName ?? Environment.CurrentDirectory,
                RootDirectory = scenarioRoot,
                StateDirectory = stateDirectory,
                LogsDirectory = Path.Combine(scenarioRoot, "logs"),
                LockFilePath = Path.Combine(stateDirectory, "scenario.lock"),
                ConfigPath = configPath,
                BalancesPath = Path.Combine(stateDirectory, "balances.json"),
                SummaryPath = Path.Combine(scenarioRoot, "summary.txt"),
                Config = config
            };
        }

        public string GetLogPath(string processName) => Path.Combine(LogsDirectory, $"{processName.ToLowerInvariant()}.log");
        public string GetContractPath(string contractId) => Path.Combine(StateDirectory, $"{contractId}.json");

        public List<HtlcContract> LoadContracts()
        {
            return Directory.GetFiles(StateDirectory, "contract_*.json")
                .Select(ReadJson<HtlcContract>)
                .OrderBy(x => x.ContractId)
                .ToList();
        }

        public Dictionary<string, Dictionary<string, decimal>> LoadBalances() =>
            ReadJson<Dictionary<string, Dictionary<string, decimal>>>(BalancesPath);

        public void SaveBalances(Dictionary<string, Dictionary<string, decimal>> balances) => WriteJson(BalancesPath, balances);
        public void SaveConfig() => WriteJson(ConfigPath, Config);
    }

    private static class PartyProcess
    {
        public static void RunPartyA(ScenarioContext context, FileLogger logger)
        {
            logger.Info("Generating secret and hash lock.");
            var secret = $"secret-{Guid.NewGuid():N}";
            var hashLock = HashSecret(secret);
            logger.Info($"Generated hash lock: {hashLock}");

            var contractAb = new HtlcContract
            {
                ContractId = "contract_ab",
                FromParty = "PartyA",
                ToParty = "PartyB",
                AssetName = "CoinA",
                Amount = 10m,
                HashLock = hashLock,
                TimeLockUtc = DateTime.UtcNow.AddSeconds(context.Config.ContractAbTimeLockSeconds),
                Status = ContractStatus.Created
            };

            ContractStorage.CreateContract(context, contractAb, logger);
            ContractStorage.FundContract(context, contractAb.ContractId, "PartyA", logger);

            logger.Info("Waiting until contract C->A is funded.");
            WaitUntil(
                () => ContractStorage.TryGetContract(context, "contract_ca", out var contract)
                      && contract.Status == ContractStatus.Funded,
                TimeSpan.FromSeconds(30),
                "contract C->A to be funded");

            if (context.Config.Kind == ScenarioKind.Success)
            {
                // PartyA reveals the secret here, which lets the other parties finish their redeem calls.
                logger.Info("Success scenario: redeeming contract C->A with the secret.");
                ContractStorage.Redeem(context, "contract_ca", "PartyA", secret, logger);
            }
            else
            {
                logger.Warn("Timeout scenario: PartyA intentionally does not redeem contract C->A.");
            }

            WaitForExpiryThenRefund(context, "contract_ab", "PartyA", logger);
            LogObservedFinalBalances(context, logger);
        }

        public static void RunPartyB(ScenarioContext context, FileLogger logger)
        {
            logger.Info("Waiting until contract A->B is funded.");
            WaitUntil(
                () => ContractStorage.TryGetContract(context, "contract_ab", out var contract)
                      && contract.Status == ContractStatus.Funded,
                TimeSpan.FromSeconds(30),
                "contract A->B to be funded");

            var source = ContractStorage.GetContract(context, "contract_ab");
            var contractBc = new HtlcContract
            {
                ContractId = "contract_bc",
                FromParty = "PartyB",
                ToParty = "PartyC",
                AssetName = "CoinB",
                Amount = 15m,
                HashLock = source.HashLock,
                TimeLockUtc = DateTime.UtcNow.AddSeconds(context.Config.ContractBcTimeLockSeconds),
                Status = ContractStatus.Created
            };

            logger.Info("Creating contract B->C using the same hash lock.");
            ContractStorage.CreateContract(context, contractBc, logger);
            ContractStorage.FundContract(context, contractBc.ContractId, "PartyB", logger);

            if (context.Config.Kind == ScenarioKind.Success)
            {
                logger.Info("Waiting for secret to appear in redeemed contract B->C.");
                WaitUntil(
                    () => ContractStorage.TryGetContract(context, "contract_bc", out var current)
                          && current.Status == ContractStatus.Redeemed
                          && !string.IsNullOrWhiteSpace(current.RevealedSecret),
                    TimeSpan.FromSeconds(30),
                    "secret revelation in contract B->C");

                var redeemedContract = ContractStorage.GetContract(context, "contract_bc");
                logger.Info("Secret was revealed by PartyC. Redeeming contract A->B.");
                ContractStorage.Redeem(context, "contract_ab", "PartyB", redeemedContract.RevealedSecret!, logger);
            }

            WaitForExpiryThenRefund(context, "contract_bc", "PartyB", logger);
            LogObservedFinalBalances(context, logger);
        }

        public static void RunPartyC(ScenarioContext context, FileLogger logger)
        {
            logger.Info("Waiting until contract B->C is funded.");
            WaitUntil(
                () => ContractStorage.TryGetContract(context, "contract_bc", out var contract)
                      && contract.Status == ContractStatus.Funded,
                TimeSpan.FromSeconds(30),
                "contract B->C to be funded");

            var source = ContractStorage.GetContract(context, "contract_bc");
            var contractCa = new HtlcContract
            {
                ContractId = "contract_ca",
                FromParty = "PartyC",
                ToParty = "PartyA",
                AssetName = "CoinC",
                Amount = 20m,
                HashLock = source.HashLock,
                TimeLockUtc = DateTime.UtcNow.AddSeconds(context.Config.ContractCaTimeLockSeconds),
                Status = ContractStatus.Created
            };

            logger.Info("Creating contract C->A using the same hash lock.");
            ContractStorage.CreateContract(context, contractCa, logger);
            ContractStorage.FundContract(context, contractCa.ContractId, "PartyC", logger);

            if (context.Config.Kind == ScenarioKind.Success)
            {
                logger.Info("Waiting for PartyA to reveal the secret in contract C->A.");
                WaitUntil(
                    () => ContractStorage.TryGetContract(context, "contract_ca", out var current)
                          && current.Status == ContractStatus.Redeemed
                          && !string.IsNullOrWhiteSpace(current.RevealedSecret),
                    TimeSpan.FromSeconds(30),
                    "secret revelation in contract C->A");

                var redeemedContract = ContractStorage.GetContract(context, "contract_ca");
                logger.Info("Using the revealed secret to redeem contract B->C.");
                ContractStorage.Redeem(context, "contract_bc", "PartyC", redeemedContract.RevealedSecret!, logger);
            }

            WaitForExpiryThenRefund(context, "contract_ca", "PartyC", logger);
            LogObservedFinalBalances(context, logger);
        }

        private static void WaitForExpiryThenRefund(ScenarioContext context, string contractId, string actor, FileLogger logger)
        {
            while (true)
            {
                if (!ContractStorage.TryGetContract(context, contractId, out var contract))
                {
                    Thread.Sleep(250);
                    continue;
                }

                if (contract.Status is ContractStatus.Redeemed or ContractStatus.Refunded)
                {
                    logger.Info($"{contractId} already finished with status {contract.Status}.");
                    return;
                }

                if ((contract.Status == ContractStatus.Funded || contract.Status == ContractStatus.Expired) && contract.IsExpired())
                {
                    logger.Warn($"Timelock expired for {contractId}. Trying refund.");
                    ContractStorage.Refund(context, contractId, actor, logger);
                    return;
                }

                Thread.Sleep(250);
            }
        }

        private static void LogObservedFinalBalances(ScenarioContext context, FileLogger logger)
        {
            var balances = context.LoadBalances();
            foreach (var party in balances.OrderBy(x => x.Key))
            {
                logger.Info($"Observed balances for {party.Key}: {FormatAssets(party.Value)}");
            }
        }
    }

    private static class ContractStorage
    {
        public static void CreateContract(ScenarioContext context, HtlcContract contract, FileLogger logger)
        {
            ExecuteLocked(context.LockFilePath, () =>
            {
                var path = context.GetContractPath(contract.ContractId);
                if (File.Exists(path))
                {
                    throw new InvalidOperationException($"Contract {contract.ContractId} already exists.");
                }

                WriteJson(path, contract);
            });

            logger.Info($"Created contract {contract.ContractId}: {contract.FromParty} -> {contract.ToParty}, {contract.Amount} {contract.AssetName}, timelock until {contract.TimeLockUtc:O}");
        }

        public static void FundContract(ScenarioContext context, string contractId, string actor, FileLogger logger)
        {
            ExecuteLocked(context.LockFilePath, () =>
            {
                var contract = GetContract(context, contractId);
                if (contract.FromParty != actor)
                {
                    throw new InvalidOperationException($"{actor} cannot fund contract {contractId}.");
                }

                if (contract.Status != ContractStatus.Created)
                {
                    throw new InvalidOperationException($"Contract {contractId} cannot be funded in status {contract.Status}.");
                }

                var balances = context.LoadBalances();
                ChangeBalance(balances, contract.FromParty, contract.AssetName, -contract.Amount);
                context.SaveBalances(balances);

                contract.Status = ContractStatus.Funded;
                contract.FundedAtUtc = DateTime.UtcNow;
                SaveContract(context, contract);
            });

            logger.Info($"Funded contract {contractId}.");
        }

        public static void Redeem(ScenarioContext context, string contractId, string actor, string secret, FileLogger logger)
        {
            ExecuteLocked(context.LockFilePath, () =>
            {
                var contract = GetContract(context, contractId);

                if (contract.ToParty != actor)
                {
                    throw new InvalidOperationException($"{actor} cannot redeem contract {contractId}.");
                }

                if (contract.Status != ContractStatus.Funded)
                {
                    throw new InvalidOperationException($"Contract {contractId} cannot be redeemed in status {contract.Status}.");
                }

                if (contract.IsExpired())
                {
                    contract.Status = ContractStatus.Expired;
                    SaveContract(context, contract);
                    throw new InvalidOperationException($"Contract {contractId} is expired.");
                }

                if (!contract.VerifySecret(secret))
                {
                    throw new InvalidOperationException($"Invalid secret for contract {contractId}.");
                }

                var balances = context.LoadBalances();
                ChangeBalance(balances, contract.ToParty, contract.AssetName, contract.Amount);
                context.SaveBalances(balances);

                contract.Status = ContractStatus.Redeemed;
                contract.RedeemedAtUtc = DateTime.UtcNow;
                contract.RevealedSecret = secret;
                SaveContract(context, contract);
            });

            logger.Info($"Redeemed contract {contractId} using the revealed secret.");
        }

        public static void Refund(ScenarioContext context, string contractId, string actor, FileLogger logger)
        {
            ExecuteLocked(context.LockFilePath, () =>
            {
                var contract = GetContract(context, contractId);

                if (contract.FromParty != actor)
                {
                    throw new InvalidOperationException($"{actor} cannot refund contract {contractId}.");
                }

                if (contract.Status == ContractStatus.Redeemed)
                {
                    throw new InvalidOperationException($"Contract {contractId} has already been redeemed.");
                }

                if (contract.Status == ContractStatus.Refunded)
                {
                    return;
                }

                if (!contract.IsExpired())
                {
                    throw new InvalidOperationException($"Contract {contractId} is not expired yet.");
                }

                var balances = context.LoadBalances();
                ChangeBalance(balances, contract.FromParty, contract.AssetName, contract.Amount);
                context.SaveBalances(balances);

                contract.Status = ContractStatus.Refunded;
                contract.RefundedAtUtc = DateTime.UtcNow;
                SaveContract(context, contract);
            });

            logger.Warn($"Refund executed for {contractId}.");
        }

        public static bool TryGetContract(ScenarioContext context, string contractId, out HtlcContract contract)
        {
            var path = context.GetContractPath(contractId);
            if (!File.Exists(path))
            {
                contract = default!;
                return false;
            }

            contract = ReadJson<HtlcContract>(path);
            return true;
        }

        public static HtlcContract GetContract(ScenarioContext context, string contractId)
        {
            var path = context.GetContractPath(contractId);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Contract file is missing: {contractId}");
            }

            return ReadJson<HtlcContract>(path);
        }

        private static void SaveContract(ScenarioContext context, HtlcContract contract)
        {
            WriteJson(context.GetContractPath(contract.ContractId), contract);
        }

        private static void ChangeBalance(
            Dictionary<string, Dictionary<string, decimal>> balances,
            string party,
            string asset,
            decimal delta)
        {
            if (!balances.TryGetValue(party, out var assets))
            {
                throw new InvalidOperationException($"Unknown party {party}.");
            }

            if (!assets.ContainsKey(asset))
            {
                assets[asset] = 0m;
            }

            var nextValue = assets[asset] + delta;
            if (nextValue < 0m)
            {
                throw new InvalidOperationException($"Insufficient balance: {party} {asset}");
            }

            assets[asset] = nextValue;
        }
    }

    private sealed class FileLogger
    {
        private readonly string _filePath;
        private readonly string _processName;

        public FileLogger(string filePath, string processName)
        {
            _filePath = filePath;
            _processName = processName;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        }

        public void Info(string message) => Write("INFO", message);
        public void Warn(string message) => Write("WARN", message);
        public void Error(string message) => Write("ERROR", message);

        private void Write(string level, string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{_processName}] [{level}] {message}";
            Console.WriteLine(line);

            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
                    break;
                }
                catch (IOException) when (attempt < 9)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    private sealed class ScenarioConfig
    {
        public required ScenarioKind Kind { get; init; }
        public required int ContractAbTimeLockSeconds { get; init; }
        public required int ContractBcTimeLockSeconds { get; init; }
        public required int ContractCaTimeLockSeconds { get; init; }

        public static ScenarioConfig Create(ScenarioKind kind)
        {
            return new ScenarioConfig
            {
                Kind = kind,
                ContractAbTimeLockSeconds = kind == ScenarioKind.Success ? 15 : 12,
                ContractBcTimeLockSeconds = kind == ScenarioKind.Success ? 10 : 8,
                ContractCaTimeLockSeconds = kind == ScenarioKind.Success ? 6 : 4
            };
        }

        public Dictionary<string, Dictionary<string, decimal>> CreateInitialBalances()
        {
            return new Dictionary<string, Dictionary<string, decimal>>
            {
                ["PartyA"] = new Dictionary<string, decimal>
                {
                    ["CoinA"] = 100m,
                    ["CoinB"] = 0m,
                    ["CoinC"] = 0m
                },
                ["PartyB"] = new Dictionary<string, decimal>
                {
                    ["CoinA"] = 0m,
                    ["CoinB"] = 100m,
                    ["CoinC"] = 0m
                },
                ["PartyC"] = new Dictionary<string, decimal>
                {
                    ["CoinA"] = 0m,
                    ["CoinB"] = 0m,
                    ["CoinC"] = 100m
                }
            };
        }
    }

    private sealed class HtlcContract
    {
        public required string ContractId { get; set; }
        public required string FromParty { get; set; }
        public required string ToParty { get; set; }
        public required string AssetName { get; set; }
        public required decimal Amount { get; set; }
        public required string HashLock { get; set; }
        public required DateTime TimeLockUtc { get; set; }
        public ContractStatus Status { get; set; }
        public DateTime? FundedAtUtc { get; set; }
        public DateTime? RedeemedAtUtc { get; set; }
        public DateTime? RefundedAtUtc { get; set; }
        public string? RevealedSecret { get; set; }

        public bool IsExpired() => DateTime.UtcNow >= TimeLockUtc;
        public bool VerifySecret(string secret) => HashSecret(secret) == HashLock;
    }

    private enum ScenarioKind
    {
        Success,
        Timeout
    }

    private enum ContractStatus
    {
        Created,
        Funded,
        Redeemed,
        Refunded,
        Expired
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string description)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"Timed out while waiting for {description}.");
    }

    private static string HashSecret(string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(bytes);
    }

    private static void ExecuteLocked(string lockFilePath, Action action)
    {
        // A single file lock is enough here because the laboratory model keeps all shared state in one scenario folder.
        Directory.CreateDirectory(Path.GetDirectoryName(lockFilePath)!);

        for (var attempt = 0; attempt < 200; attempt++)
        {
            FileStream? lockStream = null;
            try
            {
                lockStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                action();
                return;
            }
            catch (IOException) when (attempt < 199)
            {
                Thread.Sleep(25);
            }
            finally
            {
                lockStream?.Dispose();
            }
        }

        throw new IOException("Unable to acquire scenario lock.");
    }

    private static T ReadJson<T>(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<T>(json, JsonOptions)
                       ?? throw new InvalidOperationException($"Failed to deserialize {path}");
            }
            catch (Exception ex) when ((ex is IOException || ex is JsonException) && attempt < 19)
            {
                Thread.Sleep(25);
            }
        }

        throw new InvalidOperationException($"Failed to read JSON from {path}");
    }

    private static void WriteJson<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(path, json, Encoding.UTF8);
    }
}
