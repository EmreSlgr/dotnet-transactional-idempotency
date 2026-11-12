using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

Console.OutputEncoding = Encoding.UTF8;

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("==========================================================================");
Console.WriteLine("  TRANSACTIONAL IDEMPOTENCY & RETRY (MÜKERRER TAHSİLAT KORUMASI)");
Console.WriteLine("  Senaryo: Kullanıcı 1.000 TL bakiyeli hesabından 250 TL ödeme yapar.");
Console.WriteLine("           Ödeme başarılı olur fakat tam yanıt dönerken ağ kopması (timeout) yaşanır.");
Console.WriteLine("           İstemci ödemenin gerçekleşmediğini sanıp aynı isteği 3 kez yineler (Retry).");
Console.WriteLine("==========================================================================");
Console.ResetColor();
Console.WriteLine();

// -----------------------------------------------------------------------------
// 1. SENARYO: IDEMPOTENCY OLMAYAN SİSTEM (MÜKERRER ÇEKİM / KULLANICI MAĞDURİYETİ)
// -----------------------------------------------------------------------------
Console.ForegroundColor = ConsoleColor.Yellow;
Console.WriteLine(">>> 1. SENARYO: Idempotency Koruması Olmayan Standart Ödeme Motoru");
Console.ResetColor();

using (var nonIdempotentDb = CreateDatabase())
{
    var naiveProcessor = new NaivePaymentProcessor(nonIdempotentDb);

    string orderId = "ORD-2026-9901";
    decimal orderAmount = 250m;
    int accountId = 1;

    Console.WriteLine($"  [Başlangıç] Bakiye: {naiveProcessor.GetBalance(accountId)} TL | Sipariş Tutarı: {orderAmount} TL");
    Console.WriteLine();

    // İstemci 1 ilk istek + 3 otomatik retry yapar (Toplam 4 deneme)
    for (int attempt = 1; attempt <= 4; attempt++)
    {
        bool simulateTimeout = (attempt < 4); // İlk 3 istekte yanıt dönerken ağ kopsun

        var (success, replayed, currentBalance, error) = naiveProcessor.ProcessPayment(
            accountId: accountId,
            orderId: orderId,
            amount: orderAmount,
            simulateNetworkTimeout: simulateTimeout);

        if (simulateTimeout)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  [İstek #{attempt}] DB'de çekim yapıldı ama İSTEMCİDE TIMEOUT OLDU! İstemci tekrar deniyor...");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [İstek #{attempt}] Sonunda istemciye yanıt ulaştı: 200 OK!");
            Console.ResetColor();
        }
    }

    decimal finalBalance = naiveProcessor.GetBalance(accountId);
    int ledgerCount = naiveProcessor.GetLedgerCount(accountId);

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("  [FELAKET SENARYOSU - MÜKERRER TAHSİLAT (OVERCHARGED)]");
    Console.WriteLine($"  * Başlangıç Bakiyesi       : 1.000 TL");
    Console.WriteLine($"  * Yapılması Gereken Çekim  : 250 TL (Tek bir sipariş)");
    Console.WriteLine($"  * Gerçekte Çekilen Tutar   : {1000m - finalBalance} TL ({ledgerCount} defa x 250 TL)");
    Console.WriteLine($"  * Kalan Bakiye             : {finalBalance} TL");
    Console.WriteLine($"  * Veritabanı İşlem Kaydı   : {ledgerCount} adet");
    Console.WriteLine("  * SONUÇ                    : Kullanıcı tek sipariş için 4 kez ücretlendirildi!");
    Console.ResetColor();
}

Console.WriteLine();
Console.WriteLine("--------------------------------------------------------------------------");
Console.WriteLine();

// -----------------------------------------------------------------------------
// 2. SENARYO: TRANSACTIONAL IDEMPOTENCY (AYNI TRANSACTION İÇİNDE KORUMA)
// -----------------------------------------------------------------------------
Console.ForegroundColor = ConsoleColor.Yellow;
Console.WriteLine(">>> 2. SENARYO: Transactional Idempotency Korumalı Ödeme Motoru");
Console.ResetColor();

using (var idempotentDb = CreateDatabase())
{
    var safeProcessor = new TransactionalIdempotentProcessor(idempotentDb);

    string orderId = "ORD-2026-9901";
    string idempotencyKey = "idemp_key_alice_ord_9901";
    decimal orderAmount = 250m;
    int accountId = 1;

    Console.WriteLine($"  [Başlangıç] Bakiye: {safeProcessor.GetBalance(accountId)} TL | Idempotency Key: {idempotencyKey}");
    Console.WriteLine();

    for (int attempt = 1; attempt <= 4; attempt++)
    {
        bool simulateTimeout = (attempt < 4);

        var (success, replayed, currentBalance, message) = safeProcessor.ProcessPayment(
            accountId: accountId,
            orderId: orderId,
            idempotencyKey: idempotencyKey,
            amount: orderAmount,
            simulateNetworkTimeout: simulateTimeout);

        if (attempt == 1)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [İstek #{attempt}] İlk ödeme ACID Transaction ile işlendi. (Bakiye: {currentBalance} TL)");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"             Yanıt istemciye dönerken AĞ KOPTU (Timeout). İstemci tekrar deniyor...");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  [İstek #{attempt} - RETRY] Idempotency Key yakalandı! Bakiye düşülmedi, kayıtlı yanıt döndü: 200 OK (Replayed)");
            Console.ResetColor();
        }
    }

    decimal finalBalance = safeProcessor.GetBalance(accountId);
    int ledgerCount = safeProcessor.GetLedgerCount(accountId);

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("  [KUSURSUZ GÜVENLİK - IDEMPOTENCY SUCCESS]");
    Console.WriteLine($"  * Başlangıç Bakiyesi       : 1.000 TL");
    Console.WriteLine($"  * Yapılması Gereken Çekim  : 250 TL");
    Console.WriteLine($"  * Gerçekte Çekilen Tutar   : {1000m - finalBalance} TL");
    Console.WriteLine($"  * Kalan Bakiye             : {finalBalance} TL (Kusursuz korundu)");
    Console.WriteLine($"  * Veritabanı İşlem Kaydı   : {ledgerCount} adet (Yalnızca 1 kayıt)");
    Console.WriteLine("  * SONUÇ                    : 4 denemeye rağmen tek bir tahsilat yapıldı.");
    Console.ResetColor();
}

Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("==========================================================================");
Console.WriteLine("  Simülasyon tamamlandı.");
Console.WriteLine("==========================================================================");
Console.ResetColor();


// =============================================================================
// VERİTABANI VE İŞ MOTORLARI (SQLITE IN-MEMORY)
// =============================================================================

static SqliteConnection CreateDatabase()
{
    var connection = new SqliteConnection("Data Source=:memory:");
    connection.Open();

    using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
        CREATE TABLE Accounts (
            Id INTEGER PRIMARY KEY,
            Name TEXT NOT NULL,
            Balance REAL NOT NULL
        );

        CREATE TABLE AccountLedger (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            AccountId INTEGER NOT NULL,
            Amount REAL NOT NULL,
            OrderId TEXT NOT NULL,
            CreatedAt TEXT NOT NULL
        );

        CREATE TABLE IdempotentRequests (
            IdempotencyKey TEXT PRIMARY KEY,
            AccountId INTEGER NOT NULL,
            ResponsePayload TEXT NOT NULL,
            CreatedAt TEXT NOT NULL
        );

        INSERT INTO Accounts (Id, Name, Balance) VALUES (1, 'Alice Smith', 1000.0);
    ";
    cmd.ExecuteNonQuery();

    return connection;
}

public class NaivePaymentProcessor
{
    private readonly SqliteConnection _db;

    public NaivePaymentProcessor(SqliteConnection db) => _db = db;

    public (bool Success, bool Replayed, decimal Balance, string Message) ProcessPayment(
        int accountId, string orderId, decimal amount, bool simulateNetworkTimeout)
    {
        using var tx = _db.BeginTransaction();

        // 1. Bakiye kontrolü
        using var balanceCmd = _db.CreateCommand();
        balanceCmd.Transaction = tx;
        balanceCmd.CommandText = "SELECT Balance FROM Accounts WHERE Id = @Id";
        balanceCmd.Parameters.AddWithValue("@Id", accountId);
        decimal balance = Convert.ToDecimal(balanceCmd.ExecuteScalar());

        if (balance < amount)
        {
            tx.Rollback();
            return (false, false, balance, "Yetersiz Bakiye");
        }

        // 2. Bakiye düş
        using var updateCmd = _db.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = "UPDATE Accounts SET Balance = Balance - @Amount WHERE Id = @Id";
        updateCmd.Parameters.AddWithValue("@Amount", amount);
        updateCmd.Parameters.AddWithValue("@Id", accountId);
        updateCmd.ExecuteNonQuery();

        // 3. Ledger (işlem) kaydı at
        using var ledgerCmd = _db.CreateCommand();
        ledgerCmd.Transaction = tx;
        ledgerCmd.CommandText = "INSERT INTO AccountLedger (AccountId, Amount, OrderId, CreatedAt) VALUES (@AccountId, @Amount, @OrderId, @CreatedAt)";
        ledgerCmd.Parameters.AddWithValue("@AccountId", accountId);
        ledgerCmd.Parameters.AddWithValue("@Amount", amount);
        ledgerCmd.Parameters.AddWithValue("@OrderId", orderId);
        ledgerCmd.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("o"));
        ledgerCmd.ExecuteNonQuery();

        tx.Commit();

        decimal newBalance = balance - amount;

        // Ağ zaman aşımı simülasyonu
        if (simulateNetworkTimeout)
        {
            return (true, false, newBalance, "NetworkTimeoutException");
        }

        return (true, false, newBalance, "PaymentSuccessful");
    }

    public decimal GetBalance(int accountId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT Balance FROM Accounts WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", accountId);
        return Convert.ToDecimal(cmd.ExecuteScalar());
    }

    public int GetLedgerCount(int accountId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AccountLedger WHERE AccountId = @Id";
        cmd.Parameters.AddWithValue("@Id", accountId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}

public class TransactionalIdempotentProcessor
{
    private readonly SqliteConnection _db;

    public TransactionalIdempotentProcessor(SqliteConnection db) => _db = db;

    public (bool Success, bool Replayed, decimal Balance, string Message) ProcessPayment(
        int accountId, string orderId, string idempotencyKey, decimal amount, bool simulateNetworkTimeout)
    {
        using var tx = _db.BeginTransaction();

        // ADIM 1: Idempotency kontrolü - Bu anahtar daha önce işlendi mi?
        using var checkCmd = _db.CreateCommand();
        checkCmd.Transaction = tx;
        checkCmd.CommandText = "SELECT ResponsePayload FROM IdempotentRequests WHERE IdempotencyKey = @Key";
        checkCmd.Parameters.AddWithValue("@Key", idempotencyKey);
        var existingPayload = checkCmd.ExecuteScalar()?.ToString();

        if (existingPayload != null)
        {
            // Önceki başarılı sonuç doğrudan geri döndürülür!
            // Bakiye düşülmez, işlem kaydı eklenmez!
            tx.Commit();
            decimal currentBalance = GetBalance(accountId);
            return (true, true, currentBalance, existingPayload);
        }

        // ADIM 2: Bakiye Kontrolü
        using var balanceCmd = _db.CreateCommand();
        balanceCmd.Transaction = tx;
        balanceCmd.CommandText = "SELECT Balance FROM Accounts WHERE Id = @Id";
        balanceCmd.Parameters.AddWithValue("@Id", accountId);
        decimal balance = Convert.ToDecimal(balanceCmd.ExecuteScalar());

        if (balance < amount)
        {
            tx.Rollback();
            return (false, false, balance, "Yetersiz Bakiye");
        }

        // ADIM 3: Bakiye Düşümü
        using var updateCmd = _db.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = "UPDATE Accounts SET Balance = Balance - @Amount WHERE Id = @Id";
        updateCmd.Parameters.AddWithValue("@Amount", amount);
        updateCmd.Parameters.AddWithValue("@Id", accountId);
        updateCmd.ExecuteNonQuery();

        // ADIM 4: İşlem Kaydı (Ledger)
        using var ledgerCmd = _db.CreateCommand();
        ledgerCmd.Transaction = tx;
        ledgerCmd.CommandText = "INSERT INTO AccountLedger (AccountId, Amount, OrderId, CreatedAt) VALUES (@AccountId, @Amount, @OrderId, @CreatedAt)";
        ledgerCmd.Parameters.AddWithValue("@AccountId", accountId);
        ledgerCmd.Parameters.AddWithValue("@Amount", amount);
        ledgerCmd.Parameters.AddWithValue("@OrderId", orderId);
        ledgerCmd.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("o"));
        ledgerCmd.ExecuteNonQuery();

        decimal newBalance = balance - amount;

        // ADIM 5: Idempotency Tablosuna Kayıt (Aynı ACID Transaction içinde!)
        var responseObj = new
        {
            status = "Success",
            orderId = orderId,
            chargedAmount = amount,
            remainingBalance = newBalance,
            processedAt = DateTime.UtcNow
        };
        string responseJson = JsonSerializer.Serialize(responseObj);

        using var saveIdempCmd = _db.CreateCommand();
        saveIdempCmd.Transaction = tx;
        saveIdempCmd.CommandText = @"
            INSERT INTO IdempotentRequests (IdempotencyKey, AccountId, ResponsePayload, CreatedAt)
            VALUES (@Key, @AccountId, @Payload, @CreatedAt)";
        saveIdempCmd.Parameters.AddWithValue("@Key", idempotencyKey);
        saveIdempCmd.Parameters.AddWithValue("@AccountId", accountId);
        saveIdempCmd.Parameters.AddWithValue("@Payload", responseJson);
        saveIdempCmd.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("o"));
        saveIdempCmd.ExecuteNonQuery();

        // Bakiye düşümü + İşlem kaydı + Idempotency kaydı BİRLİKTE COMMIT EDİLİR!
        tx.Commit();

        if (simulateNetworkTimeout)
        {
            return (true, false, newBalance, "NetworkTimeoutException");
        }

        return (true, false, newBalance, responseJson);
    }

    public decimal GetBalance(int accountId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT Balance FROM Accounts WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", accountId);
        return Convert.ToDecimal(cmd.ExecuteScalar());
    }

    public int GetLedgerCount(int accountId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AccountLedger WHERE AccountId = @Id";
        cmd.Parameters.AddWithValue("@Id", accountId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
