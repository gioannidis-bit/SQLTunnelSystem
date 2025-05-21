using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace TdsRawProtocolCapture
{
    class Program
    {
        private static string GetTdsPacketTypeName(byte type)
        {
            switch (type)
            {
                case 0x01: return "SQL Batch";
                case 0x02: return "Pre-TDS7 Login";
                case 0x03: return "RPC";
                case 0x04: return "Tabular Result";
                case 0x06: return "Attention Signal";
                case 0x07: return "Bulk Load";
                case 0x0E: return "Transaction Manager Request";
                case 0x0F: return "TDS5 Query";
                case 0x10: return "Login";
                case 0x12: return "PreLogin";
                default: return $"Unknown (0x{type:X2})";
            }
        }

        private static string GetTdsStatusDescription(byte status)
        {
            List<string> descriptions = new List<string>();

            if ((status & 0x01) != 0) descriptions.Add("EOM (End of Message)");
            if ((status & 0x02) != 0) descriptions.Add("IGNORE");
            if ((status & 0x04) != 0) descriptions.Add("RESET_CONNECTION");
            if ((status & 0x08) != 0) descriptions.Add("RESET_CONNECTION_SKIP_TRAN");

            return descriptions.Count > 0 ? string.Join(", ", descriptions) : "Normal";
        }


        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("TDS Raw Protocol Capture - Προηγμένη καταγραφή για ανάπτυξη TDS Listener");
            Console.WriteLine("=========================================================================");

            // Αρχεία καταγραφής
            string rawDataPath = "tds_raw_packets.bin";
            string hexDumpPath = "tds_hex_dump.txt";
            string analysisPath = "tds_protocol_analysis.txt";

            // Χρησιμοποιούμε manual διαχείριση των streams για να αποφύγουμε ObjectDisposedException
            FileStream rawDataFile = null;
            StreamWriter hexDumpFile = null;
            StreamWriter analysisFile = null;

            try
            {
                rawDataFile = new FileStream(rawDataPath, FileMode.Create);
                hexDumpFile = new StreamWriter(hexDumpPath, false, Encoding.UTF8);
                analysisFile = new StreamWriter(analysisPath, false, Encoding.UTF8);

                // Ενεργοποίηση της καταγραφής δικτύου και SQL
                EnableAdvancedTracing();

                // Ρύθμιση του packet sniffer
                var packetCapture = new TdsPacketCapture(rawDataFile, hexDumpFile, analysisFile);
                packetCapture.Start();

                // Λήψη connection string από τον χρήστη
                Console.WriteLine("\nΠαρακαλώ εισάγετε το connection string για τον SQL Server:");
                Console.WriteLine("(ή πιέστε Enter για το προεπιλεγμένο: Data Source=localhost;Initial Catalog=master;Integrated Security=True;Encrypt=False)");
                Console.Write("> ");
                string connectionString = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    connectionString = "Data Source=demosrv;user id=proteluser;password=protel915930;database=protel;Encrypt=False;TrustServerCertificate=True;";
                }

                // Βελτιστοποίηση του connection string για καταγραφή
                if (!connectionString.Contains("Encrypt="))
                {
                    connectionString += ";Encrypt=False";
                }
                connectionString += ";Application Name=TdsProtocolAnalyzer;Packet Size=4096";

                Console.WriteLine($"Χρήση connection string: {connectionString}");
                analysisFile.WriteLine("=== TDS PROTOCOL ANALYSIS SESSION ===");
                analysisFile.WriteLine($"Timestamp: {DateTime.Now}");
                analysisFile.WriteLine($"Connection String: {connectionString}");
                analysisFile.WriteLine("===================================\n");

                // Εκτέλεση των δοκιμών για όλες τις φάσεις του TDS
                await ExecuteTdsProtocolTests(connectionString, packetCapture);

                // Τερματισμός της καταγραφής
                packetCapture.Stop();

                // Κλείστε ρητά το αρχείο
                rawDataFile.Close();
                rawDataFile.Dispose();

                // Περιμένετε λίγο για να βεβαιωθείτε ότι το αρχείο έχει απελευθερωθεί
                await Task.Delay(100);

                // Ανάλυση των καταγεγραμμένων πακέτων
                Console.WriteLine("\nΑνάλυση και ταξινόμηση των καταγεγραμμένων πακέτων TDS...");
                await AnalyzeCapturedTdsPackets(rawDataPath, analysisFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Κρίσιμο σφάλμα: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                if (analysisFile != null && analysisFile.BaseStream.CanWrite)
                {
                    analysisFile.WriteLine($"ΚΡΙΣΙΜΟ ΣΦΑΛΜΑ: {ex.Message}");
                    analysisFile.WriteLine(ex.StackTrace);
                }
            }
            finally
            {
                // Ασφαλές κλείσιμο όλων των streams
                try { hexDumpFile?.Flush(); hexDumpFile?.Close(); } catch { }
                try { analysisFile?.Flush(); analysisFile?.Close(); } catch { }
                try { rawDataFile?.Flush(); rawDataFile?.Close(); } catch { }
            }

            Console.WriteLine("\nΗ καταγραφή ολοκληρώθηκε. Αρχεία εξόδου:");
            Console.WriteLine($"1. Raw δυαδικά πακέτα TDS: {Path.GetFullPath(rawDataPath)}");
            Console.WriteLine($"2. Hex dump των πακέτων: {Path.GetFullPath(hexDumpPath)}");
            Console.WriteLine($"3. Ανάλυση πρωτοκόλλου: {Path.GetFullPath(analysisPath)}");
            Console.WriteLine("\nΠατήστε οποιοδήποτε πλήκτρο για έξοδο...");
            Console.ReadKey();
        }

        #region Network Tracing Setup

        static void EnableAdvancedTracing()
        {
            // Ενεργοποίηση διακοπτών για λεπτομερή καταγραφή SQL
            AppContext.SetSwitch("System.Data.SqlClient.EnableTracing", true);
            AppContext.SetSwitch("System.Data.SqlClient.EnableVerboseProtocolTracing", true);

            // Ενεργοποίηση network tracing
            TraceSource networkTrace = new TraceSource("System.Net");
            networkTrace.Switch.Level = SourceLevels.Verbose | SourceLevels.ActivityTracing;

            TraceSource socketTrace = new TraceSource("System.Net.Sockets");
            socketTrace.Switch.Level = SourceLevels.Verbose | SourceLevels.ActivityTracing;

            // Ενεργοποίηση SQL client tracing
            TraceSource sqlTrace = new TraceSource("System.Data.SqlClient");
            sqlTrace.Switch.Level = SourceLevels.Verbose | SourceLevels.ActivityTracing;

            // Ρύθμιση Auto-flush για άμεση καταγραφή
            Trace.AutoFlush = true;
        }

        #endregion

        #region TDS Protocol Tests

        static async Task ExecuteTdsProtocolTests(string connectionString, TdsPacketCapture packetCapture)
        {
            Console.WriteLine("\nΈναρξη καταγραφής όλων των φάσεων του TDS πρωτοκόλλου...");

            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    // Ενεργοποίηση στατιστικών
                    connection.StatisticsEnabled = true;

                    // Καταγραφή των SQL μηνυμάτων
                    connection.InfoMessage += (sender, e) => {
                        Console.WriteLine($"[SQL Info] {e.Message}");
                        packetCapture.LogEvent("SQL_INFO_MESSAGE", e.Message);
                    };

                    // ΦΑΣΗ 1: PreLogin & Login
                    Console.WriteLine("\n[Φάση 1] PreLogin & Login - Σύνδεση στον SQL Server...");
                    packetCapture.BeginPhase("PRELOGIN_LOGIN", "Άνοιγμα σύνδεσης - Πακέτα TDS 0x12 & 0x10");

                    // Άνοιγμα της σύνδεσης (αυτό ενεργοποιεί PreLogin και Login)
                    await connection.OpenAsync();
                    Console.WriteLine($"Σύνδεση επιτυχής! Server Version: {connection.ServerVersion}");

                    // Λήψη στατιστικών
                    var stats = connection.RetrieveStatistics();
                    packetCapture.LogStats(stats);

                    // Μικρή παύση για να διαχωριστούν τα πακέτα
                    await Task.Delay(500);

                    // ΦΑΣΗ 2: SQL Batch
                    Console.WriteLine("\n[Φάση 2] SQL Batch - Εκτέλεση SQL ερωτημάτων...");
                    packetCapture.BeginPhase("SQL_BATCH", "Εκτέλεση SQL ερωτήματος - Πακέτα TDS 0x01");

                    // Εκτέλεση διαφόρων SQL ερωτημάτων για να δούμε διαφορετικά patterns
                    string[] sqlQueries = new string[] {
                        "SELECT @@VERSION",
                        "SELECT @@SERVERNAME, DB_NAME(), HOST_NAME(), SUSER_NAME()",
                        "SELECT name, database_id, create_date FROM sys.databases",
                        "SELECT GETDATE() AS CurrentDateTime"
                    };

                    foreach (string sql in sqlQueries)
                    {
                        Console.WriteLine($"Εκτέλεση SQL: {sql}");
                        packetCapture.LogEvent("SQL_QUERY", sql);

                        using (var command = new SqlCommand(sql, connection))
                        {
                            using (var reader = await command.ExecuteReaderAsync())
                            {
                                int rows = 0;
                                while (await reader.ReadAsync())
                                {
                                    // Καταγραφή μόνο των πρώτων γραμμών
                                    if (rows < 3)
                                    {
                                        StringBuilder rowData = new StringBuilder();
                                        for (int i = 0; i < reader.FieldCount; i++)
                                        {
                                            rowData.Append($"{reader.GetName(i)}={reader[i]}, ");
                                        }
                                        Console.WriteLine($"  Row: {rowData}");
                                        packetCapture.LogEvent("SQL_RESULT_ROW", rowData.ToString());
                                    }
                                    rows++;
                                }
                                Console.WriteLine($"  Total rows: {rows}");
                            }
                        }

                        // Μικρή παύση μεταξύ ερωτημάτων
                        await Task.Delay(100);
                    }

                    // Μικρή παύση για να διαχωριστούν τα πακέτα
                    await Task.Delay(500);

                    // ΦΑΣΗ 3: RPC (Stored Procedure)
                    Console.WriteLine("\n[Φάση 3] RPC - Εκτέλεση stored procedures...");
                    packetCapture.BeginPhase("RPC", "Εκτέλεση Stored Procedure - Πακέτα TDS 0x03");

                    // Εκτέλεση διαφόρων stored procedures
                    string[] procedures = new string[] {
                        "sp_server_info",
                        "sp_databases",
                        "sp_who"
                    };

                    foreach (string proc in procedures)
                    {
                        Console.WriteLine($"Εκτέλεση procedure: {proc}");
                        packetCapture.LogEvent("RPC_CALL", proc);

                        using (var command = new SqlCommand(proc, connection))
                        {
                            command.CommandType = CommandType.StoredProcedure;

                            using (var reader = await command.ExecuteReaderAsync())
                            {
                                int rows = 0;
                                while (await reader.ReadAsync() && rows < 3)
                                {
                                    // Καταγραφή μόνο των πρώτων γραμμών
                                    StringBuilder rowData = new StringBuilder();
                                    for (int i = 0; i < reader.FieldCount; i++)
                                    {
                                        if (i < 3) // Περιορισμός στις πρώτες 3 στήλες για συντομία
                                        {
                                            rowData.Append($"{reader.GetName(i)}={reader[i]}, ");
                                        }
                                    }
                                    Console.WriteLine($"  Row: {rowData}");
                                    packetCapture.LogEvent("RPC_RESULT_ROW", rowData.ToString());
                                    rows++;
                                }

                                // Υπολογισμός συνολικού αριθμού γραμμών
                                while (await reader.ReadAsync())
                                {
                                    rows++;
                                }
                                Console.WriteLine($"  Total rows: {rows}");
                            }
                        }

                        // Μικρή παύση μεταξύ procedures
                        await Task.Delay(100);
                    }

                    // ΦΑΣΗ 4: Parameterized RPC
                    Console.WriteLine("\n[Φάση 4] Parameterized RPC - Εκτέλεση παραμετροποιημένων διαδικασιών...");
                    packetCapture.BeginPhase("PARAMETERIZED_RPC", "Εκτέλεση παραμετροποιημένης κλήσης - TDS 0x03 με παραμέτρους");

                    // Εκτέλεση sp_executesql που χρησιμοποιεί παραμέτρους
                    using (var command = new SqlCommand("sp_executesql", connection))
                    {
                        command.CommandType = CommandType.StoredProcedure;

                        // Προσθήκη παραμέτρων
                        command.Parameters.AddWithValue("@stmt", "SELECT name, database_id FROM sys.databases WHERE database_id > @id");
                        command.Parameters.AddWithValue("@params", "@id int");
                        command.Parameters.AddWithValue("@id", 3);

                        Console.WriteLine("Εκτέλεση sp_executesql με παραμέτρους");
                        packetCapture.LogEvent("PARAM_RPC_CALL", "sp_executesql με παραμέτρους");

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                Console.WriteLine($"  {reader["name"]} (ID: {reader["database_id"]})");
                            }
                        }
                    }

                    // Μικρή παύση για να διαχωριστούν τα πακέτα
                    await Task.Delay(500);

                    // ΦΑΣΗ 5: Attention Signal (Cancel)
                    Console.WriteLine("\n[Φάση 5] Attention Signal - Διακοπή ερωτήματος...");
                    packetCapture.BeginPhase("ATTENTION", "Αποστολή σήματος διακοπής - Πακέτα TDS 0x06");

                    // Δημιουργία και ακύρωση εντολής μεγάλης διάρκειας
                    using (var longCommand = new SqlCommand("WAITFOR DELAY '00:00:05'", connection))
                    {
                        // Εκτέλεση σε άλλο thread ώστε να μπορούμε να το ακυρώσουμε
                        var task = Task.Run(() => {
                            try
                            {
                                Console.WriteLine("Εκτέλεση μεγάλης διάρκειας εντολής: WAITFOR DELAY '00:00:05'");
                                packetCapture.LogEvent("LONG_QUERY", "WAITFOR DELAY '00:00:05'");
                                longCommand.ExecuteNonQuery();
                                return true;
                            }
                            catch (SqlException ex)
                            {
                                Console.WriteLine($"Εντολή διακόπηκε: {ex.Message}");
                                packetCapture.LogEvent("QUERY_CANCELLED", ex.Message);
                                return false;
                            }
                        });

                        // Περιμένουμε λίγο και μετά στέλνουμε cancel
                        await Task.Delay(1000);
                        Console.WriteLine("Αποστολή Attention Signal (Cancel)...");
                        packetCapture.LogEvent("SENDING_CANCEL", "Αποστολή Attention Signal");
                        longCommand.Cancel();

                        // Περιμένουμε το αποτέλεσμα
                        bool completed = await task;
                        Console.WriteLine($"Η εντολή {(completed ? "ολοκληρώθηκε" : "διακόπηκε")}");
                    }

                    // Μικρή παύση για να διαχωριστούν τα πακέτα
                    await Task.Delay(500);

                    // ΦΑΣΗ 6: Transaction Management
                    Console.WriteLine("\n[Φάση 6] Transaction Management - Διαχείριση συναλλαγών...");
                    packetCapture.BeginPhase("TRANSACTION", "Διαχείριση συναλλαγών - Πακέτα TDS 0x0E");

                    // Δημιουργία συναλλαγής
                    using (var transaction = connection.BeginTransaction())
                    {
                        Console.WriteLine($"Συναλλαγή ξεκίνησε: {transaction.IsolationLevel}");
                        packetCapture.LogEvent("BEGIN_TRANSACTION", $"Isolation Level: {transaction.IsolationLevel}");

                        // Εκτέλεση εντολής μέσα στη συναλλαγή
                        using (var cmd = new SqlCommand("SELECT @@TRANCOUNT", connection, transaction))
                        {
                            var tranCount = await cmd.ExecuteScalarAsync();
                            Console.WriteLine($"@@TRANCOUNT = {tranCount}");
                            packetCapture.LogEvent("TRANSACTION_COMMAND", $"@@TRANCOUNT = {tranCount}");
                        }

                        // Rollback συναλλαγής
                        Console.WriteLine("Εκτέλεση ROLLBACK TRANSACTION");
                        packetCapture.LogEvent("ROLLBACK_TRANSACTION", "Explicit rollback");
                        transaction.Rollback();
                    }

                    // Μικρή παύση για να διαχωριστούν τα πακέτα
                    await Task.Delay(500);

                    // ΦΑΣΗ 7: Bulk Copy
                    Console.WriteLine("\n[Φάση 7] Bulk Copy - Μαζική εισαγωγή δεδομένων...");
                    packetCapture.BeginPhase("BULK_COPY", "Μαζική εισαγωγή δεδομένων - Πακέτα TDS 0x07");

                    try
                    {
                        // Δημιουργία προσωρινού πίνακα
                        using (var cmd = new SqlCommand(
                            "IF OBJECT_ID('tempdb..#TdsTestTable') IS NOT NULL DROP TABLE #TdsTestTable; " +
                            "CREATE TABLE #TdsTestTable (ID int, Name nvarchar(50), CreateDate datetime);",
                            connection))
                        {
                            await cmd.ExecuteNonQueryAsync();
                            packetCapture.LogEvent("TEMP_TABLE_CREATED", "#TdsTestTable");
                        }

                        // Δημιουργία DataTable με δεδομένα
                        DataTable dataTable = new DataTable("TestData");
                        dataTable.Columns.Add("ID", typeof(int));
                        dataTable.Columns.Add("Name", typeof(string));
                        dataTable.Columns.Add("CreateDate", typeof(DateTime));

                        // Προσθήκη δεδομένων
                        for (int i = 1; i <= 10; i++)
                        {
                            dataTable.Rows.Add(i, $"Item {i}", DateTime.Now.AddDays(-i));
                        }

                        // Εκτέλεση του bulk copy
                        Console.WriteLine("Εκτέλεση Bulk Copy για 10 γραμμές...");
                        packetCapture.LogEvent("BULK_COPY_START", "10 γραμμές με 3 στήλες");

                        using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection))
                        {
                            bulkCopy.DestinationTableName = "#TdsTestTable";
                            await bulkCopy.WriteToServerAsync(dataTable);
                        }

                        // Επαλήθευση του bulk copy
                        using (var cmd = new SqlCommand("SELECT COUNT(*) FROM #TdsTestTable", connection))
                        {
                            var rowCount = await cmd.ExecuteScalarAsync();
                            Console.WriteLine($"Bulk Copy ολοκληρώθηκε - {rowCount} γραμμές εισήχθησαν");
                            packetCapture.LogEvent("BULK_COPY_COMPLETE", $"{rowCount} γραμμές εισήχθησαν");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Σφάλμα στο Bulk Copy: {ex.Message}");
                        packetCapture.LogEvent("BULK_COPY_ERROR", ex.Message);
                    }

                    // ΦΑΣΗ 8: Κλείσιμο σύνδεσης
                    Console.WriteLine("\n[Φάση 8] Connection Close - Τερματισμός σύνδεσης...");
                    packetCapture.BeginPhase("CONNECTION_CLOSE", "Κλείσιμο σύνδεσης");

                    // Λήψη τελικών στατιστικών
                    stats = connection.RetrieveStatistics();
                    packetCapture.LogStats(stats);
                }

                Console.WriteLine("\nΟι δοκιμές του TDS πρωτοκόλλου ολοκληρώθηκαν επιτυχώς!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Σφάλμα κατά την εκτέλεση των δοκιμών TDS: {ex.Message}");
                packetCapture.LogEvent("ERROR", ex.Message);
            }
        }

        #endregion

        #region Packet Analysis

        static async Task AnalyzeCapturedTdsPackets(string rawDataPath, StreamWriter analysisFile)
        {
            analysisFile.WriteLine("\n\n====== ΣΥΝΟΠΤΙΚΉ ΑΝΆΛΥΣΗ TDS ΠΑΚΈΤΩΝ ======");

            try
            {
                using (var file = new FileStream(rawDataPath, FileMode.Open, FileAccess.Read))
                {
                    long fileSize = file.Length;
                    analysisFile.WriteLine($"Συνολικό μέγεθος καταγεγραμμένων δεδομένων: {fileSize} bytes");

                    // Ανάλυση των πρώτων bytes για την επικεφαλίδα TDS
                    if (fileSize >= 8)
                    {
                        byte[] header = new byte[8];
                        await file.ReadAsync(header, 0, 8);
                        file.Position = 0; // Επαναφορά στην αρχή

                        // Έλεγχος για TDS header signature
                        if (header[0] >= 1 && header[0] <= 0x12)
                        {
                            analysisFile.WriteLine("\nΑναγνωρίστηκαν πακέτα TDS!");
                            analysisFile.WriteLine("Πρώτη επικεφαλίδα TDS:");
                            analysisFile.WriteLine($"  Type: 0x{header[0]:X2} ({GetTdsPacketTypeName(header[0])})");
                            analysisFile.WriteLine($"  Status: 0x{header[1]:X2} ({GetTdsStatusDescription(header[1])})");
                            analysisFile.WriteLine($"  Length: {BitConverter.ToUInt16(header, 2)} bytes");
                            analysisFile.WriteLine($"  SPID: {BitConverter.ToUInt16(header, 4)}");
                            analysisFile.WriteLine($"  Packet ID: {header[6]}");
                            analysisFile.WriteLine($"  Window: {header[7]}");
                        }
                    }

                    // Συνοπτική ανάλυση τύπων πακέτων
                    analysisFile.WriteLine("\nΟδηγίες χρήσης για ανάπτυξη TDS Listener:");
                    analysisFile.WriteLine("1. Η επικεφαλίδα TDS είναι πάντα 8 bytes");
                    analysisFile.WriteLine("2. Βασικοί τύποι πακέτων που πρέπει να υποστηρίζει ο listener:");
                    analysisFile.WriteLine("   - 0x12: PreLogin - Αρχική επικοινωνία πριν τη σύνδεση");
                    analysisFile.WriteLine("   - 0x10: Login - Αποστολή διαπιστευτηρίων");
                    analysisFile.WriteLine("   - 0x01: SQL Batch - Εκτέλεση SQL ερωτημάτων");
                    analysisFile.WriteLine("   - 0x03: RPC - Κλήση stored procedures");
                    analysisFile.WriteLine("   - 0x06: Attention - Διακοπή τρέχοντος ερωτήματος");
                    analysisFile.WriteLine("   - 0x07: Bulk Copy - Μαζική εισαγωγή δεδομένων");

                    analysisFile.WriteLine("\n3. Εξαγωγή SQL ερωτημάτων:");
                    analysisFile.WriteLine("   - Στα πακέτα τύπου 0x01 (SQL Batch), το SQL ερώτημα αρχίζει μετά από 8 bytes επικεφαλίδα + 4 bytes headers");
                    analysisFile.WriteLine("   - Το κείμενο είναι σε κωδικοποίηση Unicode (UTF-16LE)");
                    analysisFile.WriteLine("   - Παράδειγμα κώδικα εξαγωγής:");
                    analysisFile.WriteLine("     if (packetType == 0x01 && packetLength > 12) {");
                    analysisFile.WriteLine("         byte[] queryBytes = new byte[packetLength - 12];");
                    analysisFile.WriteLine("         Array.Copy(packet, 12, queryBytes, 0, queryBytes.Length);");
                    analysisFile.WriteLine("         string sqlQuery = Encoding.Unicode.GetString(queryBytes).TrimEnd('\\0');");
                    analysisFile.WriteLine("         // Process sqlQuery...");
                    analysisFile.WriteLine("     }");

                    analysisFile.WriteLine("\n4. Εξαγωγή RPC κλήσεων:");
                    analysisFile.WriteLine("   - Πακέτα τύπου 0x03 (RPC)");
                    analysisFile.WriteLine("   - Μετά την επικεφαλίδα 8 bytes + 4 bytes headers, ακολουθούν οι πληροφορίες procedure");
                    analysisFile.WriteLine("   - Το όνομα procedure βρίσκεται μετά από 2 bytes μήκους ονόματος");

                    analysisFile.WriteLine("\n5. Απαντήσεις SQL Server (τύπος 0x04):");
                    analysisFile.WriteLine("   - Περιέχουν διάφορα tokens: 0x81 (COLMETADATA), 0xD1 (ROW), 0xFD (DONE), κλπ.");
                    analysisFile.WriteLine("   - Για έναν απλό TDS listener, μπορείτε να στέλνετε προσομοιωμένες απαντήσεις");

                    analysisFile.WriteLine("\nΑνατρέξτε στο αρχείο tds_hex_dump.txt για πλήρη hex dump των πακέτων TDS και του περιεχομένου τους.");
                }
            }
            catch (Exception ex)
            {
                analysisFile.WriteLine($"Σφάλμα κατά την ανάλυση: {ex.Message}");
            }
        }

       

        #endregion
    }

    #region TDS Packet Capture Implementation

    /// <summary>
    /// Κλάση για καταγραφή και ανάλυση των πακέτων TDS
    /// </summary>
    public class TdsPacketCapture : IDisposable
    {
        private static string GetTdsPacketTypeName(byte type)
        {
            switch (type)
            {
                case 0x01: return "SQL Batch";
                case 0x02: return "Pre-TDS7 Login";
                case 0x03: return "RPC";
                case 0x04: return "Tabular Result";
                case 0x06: return "Attention Signal";
                case 0x07: return "Bulk Load";
                case 0x0E: return "Transaction Manager Request";
                case 0x0F: return "TDS5 Query";
                case 0x10: return "Login";
                case 0x12: return "PreLogin";
                default: return $"Unknown (0x{type:X2})";
            }
        }

        private static string GetTdsStatusDescription(byte status)
        {
            List<string> descriptions = new List<string>();

            if ((status & 0x01) != 0) descriptions.Add("EOM (End of Message)");
            if ((status & 0x02) != 0) descriptions.Add("IGNORE");
            if ((status & 0x04) != 0) descriptions.Add("RESET_CONNECTION");
            if ((status & 0x08) != 0) descriptions.Add("RESET_CONNECTION_SKIP_TRAN");

            return descriptions.Count > 0 ? string.Join(", ", descriptions) : "Normal";
        }

        // Streams για καταγραφή δεδομένων
        private FileStream _rawDataStream;
        private StreamWriter _hexDumpWriter;
        private StreamWriter _analysisWriter;

        // Flags και στατιστικά
        private bool _isRunning = false;
        private int _packetCount = 0;
        private DateTime _currentPhaseStartTime;
        private string _currentPhase = "";
        private bool _packetHeaderWritten = false;

        // Proxy για την παρακολούθηση της κίνησης του δικτύου
        private TcpProxy _tcpProxy = null;

        /// <summary>
        /// Δημιουργεί ένα νέο αντικείμενο καταγραφής πακέτων TDS
        /// </summary>
        public TdsPacketCapture(FileStream rawDataStream, StreamWriter hexDumpWriter, StreamWriter analysisWriter)
        {
            _rawDataStream = rawDataStream;
            _hexDumpWriter = hexDumpWriter;
            _analysisWriter = analysisWriter;
        }

        /// <summary>
        /// Ξεκινά την καταγραφή των πακέτων TDS
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            _isRunning = true;
            _packetCount = 0;

            // Γράψιμο επικεφαλίδας στα αρχεία
            WriteHeaders();

            // Ρύθμιση του .NET Network tracing για καταγραφή διαδικτυακής κίνησης
            SetupNetworkTracing();

            // Προσθήκη event handler για τα πακέτα TDS (SQL Client)
            SetupSqlClientTracing();

            Console.WriteLine("Καταγραφή πακέτων TDS ενεργοποιήθηκε");
        }

        private void WriteHeaders()
        {
            if (_packetHeaderWritten) return;

            // Γράψιμο επικεφαλίδας στο αρχείο raw data
            byte[] header = Encoding.ASCII.GetBytes("TDS_RAW_PACKET_CAPTURE\r\n");
            _rawDataStream.Write(header, 0, header.Length);
            _rawDataStream.Flush();

            // Γράψιμο επικεφαλίδας στο hex dump
            _hexDumpWriter.WriteLine("===============================");
            _hexDumpWriter.WriteLine("= TDS Protocol Hex Dump File =");
            _hexDumpWriter.WriteLine("===============================");
            _hexDumpWriter.WriteLine("Timestamp: " + DateTime.Now.ToString());
            _hexDumpWriter.WriteLine("Format: [Packet#] [Direction] [Type] [Length] [Hex Dump] [ASCII]");
            _hexDumpWriter.WriteLine("===============================\n");
            _hexDumpWriter.Flush();

            _packetHeaderWritten = true;
        }

        private void SetupNetworkTracing()
        {
            // Βασική παρακολούθηση δικτύου με trace sources
            TraceSource networkTrace = new TraceSource("System.Net");
            ConsoleTraceListener networkListener = new ConsoleTraceListener(false);
            networkTrace.Listeners.Add(networkListener);

            // Δημιουργία TCP proxy για παρακολούθηση SQL πακέτων
            try
            {
                // Σημείωση: Στην πραγματικότητα θα υλοποιούσαμε έναν πραγματικό TCP proxy
                // για να παρακολουθούμε όλη την κίνηση TDS, αλλά για λόγους απλότητας
                // χρησιμοποιούμε άλλες μεθόδους στο παράδειγμα.
                _tcpProxy = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Προειδοποίηση: Αδυναμία δημιουργίας TCP proxy: {ex.Message}");
            }
        }

        private void SetupSqlClientTracing()
        {
            // Προσθήκη διακοπτών για καταγραφή ADO.NET
           

            // Υποκατάσταση του trace output για καταγραφή των TDS συμβάντων
            TextWriter originalConsoleOut = Console.Out;
            Console.SetOut(new TdsTraceInterceptor(originalConsoleOut, this));
        }

        /// <summary>
        /// Καταγράφει ένα συμβάν στο αρχείο ανάλυσης
        /// </summary>
        public void LogEvent(string eventType, string message)
        {
            if (!_isRunning || _analysisWriter == null) return;

            try
            {
                _analysisWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{_currentPhase}] [{eventType}] {message}");
                _analysisWriter.Flush();
            }
            catch { }
        }

        /// <summary>
        /// Καταγράφει στατιστικά σύνδεσης
        /// </summary>
        public void LogStats(IDictionary stats)
        {
            if (!_isRunning || _analysisWriter == null) return;

            try
            {
                _analysisWriter.WriteLine($"\n=== SQL Connection Statistics - Phase: {_currentPhase} ===");
                foreach (DictionaryEntry stat in stats)
                {
                    _analysisWriter.WriteLine($"  {stat.Key}: {stat.Value}");
                }
                _analysisWriter.WriteLine("");
                _analysisWriter.Flush();
            }
            catch { }
        }

        /// <summary>
        /// Ξεκινάει μια νέα φάση καταγραφής
        /// </summary>
        public void BeginPhase(string phaseName, string description)
        {
            if (!_isRunning) return;

            try
            {
                _currentPhase = phaseName;
                _currentPhaseStartTime = DateTime.Now;

                // Καταγραφή στο αρχείο ανάλυσης
                _analysisWriter.WriteLine($"\n\n===== PHASE: {phaseName} =====");
                _analysisWriter.WriteLine($"Start Time: {_currentPhaseStartTime:HH:mm:ss.fff}");
                _analysisWriter.WriteLine($"Description: {description}");
                _analysisWriter.WriteLine("=============================");
                _analysisWriter.Flush();

                // Καταγραφή στο hex dump
                _hexDumpWriter.WriteLine($"\n\n// ===== PHASE: {phaseName} =====");
                _hexDumpWriter.WriteLine($"// {description}");
                _hexDumpWriter.WriteLine($"// Start Time: {_currentPhaseStartTime:HH:mm:ss.fff}");
                _hexDumpWriter.WriteLine("// =============================\n");
                _hexDumpWriter.Flush();
            }
            catch { }
        }

        /// <summary>
        /// Καταγράφει ένα πακέτο TDS
        /// </summary>
        public void CapturePacket(byte[] packet, string direction, string description = "")
        {
            if (!_isRunning || packet == null || packet.Length < 8) return;

            try
            {
                _packetCount++;

                // Εξαγωγή πληροφοριών από την επικεφαλίδα TDS
                byte packetType = packet[0];
                byte statusFlags = packet[1];
                ushort length = BitConverter.ToUInt16(packet, 2);
                ushort spid = BitConverter.ToUInt16(packet, 4);
                byte packetId = packet[6];
                byte window = packet[7];

                string packetTypeName = GetTdsPacketTypeName(packetType);
                string statusDesc = GetTdsStatusDescription(statusFlags);

                // Καταγραφή του raw πακέτου στο αρχείο
                // Προσθήκη μιας επικεφαλίδας καταγραφής (16 bytes)
                byte[] captureHeader = new byte[16];
                Array.Copy(BitConverter.GetBytes(DateTime.Now.Ticks), 0, captureHeader, 0, 8);
                Array.Copy(BitConverter.GetBytes(packet.Length), 0, captureHeader, 8, 4);
                Array.Copy(BitConverter.GetBytes((int)_packetCount), 0, captureHeader, 12, 4);

                _rawDataStream.Write(captureHeader, 0, captureHeader.Length);
                _rawDataStream.Write(packet, 0, packet.Length);
                _rawDataStream.Flush();

                // Δημιουργία hex dump του πακέτου
                _hexDumpWriter.WriteLine($"// Packet #{_packetCount} - {direction} - {packetTypeName} - {length} bytes");
                _hexDumpWriter.WriteLine($"// TDS Header: Type=0x{packetType:X2}, Status=0x{statusFlags:X2} ({statusDesc}), Length={length}, SPID={spid}, PacketID={packetId}, Window={window}");

                if (!string.IsNullOrEmpty(description))
                {
                    _hexDumpWriter.WriteLine($"// Description: {description}");
                }

                // Εκτύπωση του hex dump με offset, hex και ASCII
                for (int i = 0; i < packet.Length; i += 16)
                {
                    // Offset
                    _hexDumpWriter.Write($"{i:X4}: ");

                    // Hex values
                    for (int j = 0; j < 16; j++)
                    {
                        if (i + j < packet.Length)
                            _hexDumpWriter.Write($"{packet[i + j]:X2} ");
                        else
                            _hexDumpWriter.Write("   ");

                        if (j == 7) _hexDumpWriter.Write(" ");
                    }

                    // ASCII representation
                    _hexDumpWriter.Write(" | ");
                    for (int j = 0; j < 16; j++)
                    {
                        if (i + j < packet.Length)
                        {
                            char c = (char)packet[i + j];
                            if (c >= 32 && c <= 126)
                                _hexDumpWriter.Write(c);
                            else
                                _hexDumpWriter.Write(".");
                        }
                    }

                    _hexDumpWriter.WriteLine();
                }

                _hexDumpWriter.WriteLine();
                _hexDumpWriter.Flush();

                // Ανάλυση του πακέτου ανάλογα με τον τύπο του
                AnalyzePacket(packet, packetType, direction);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Σφάλμα κατά την καταγραφή πακέτου: {ex.Message}");
            }
        }

        private void AnalyzePacket(byte[] packet, byte packetType, string direction)
        {
            try
            {
                _analysisWriter.WriteLine($"\n--- TDS Packet #{_packetCount} ({direction}) ---");
                _analysisWriter.WriteLine($"Type: 0x{packetType:X2} ({GetTdsPacketTypeName(packetType)})");
                _analysisWriter.WriteLine($"Size: {packet.Length} bytes");
                _analysisWriter.WriteLine($"Time: {DateTime.Now:HH:mm:ss.fff}");

                // Ανάλυση ανάλογα με τον τύπο του πακέτου
                switch (packetType)
                {
                    case 0x01: // SQL Batch
                        AnalyzeSqlBatchPacket(packet);
                        break;
                    case 0x03: // RPC
                        AnalyzeRpcPacket(packet);
                        break;
                    case 0x04: // Tabular Result
                        AnalyzeResultPacket(packet);
                        break;
                    case 0x12: // PreLogin
                        AnalyzePreLoginPacket(packet);
                        break;
                    case 0x10: // Login
                        AnalyzeLoginPacket(packet);
                        break;
                    default:
                        _analysisWriter.WriteLine("Δεν υλοποιήθηκε λεπτομερής ανάλυση για αυτόν τον τύπο πακέτου.");
                        break;
                }

                _analysisWriter.Flush();
            }
            catch (Exception ex)
            {
                _analysisWriter.WriteLine($"Σφάλμα κατά την ανάλυση πακέτου: {ex.Message}");
            }
        }

        private void AnalyzeSqlBatchPacket(byte[] packet)
        {
            _analysisWriter.WriteLine("\n=== SQL BATCH ANALYSIS ===");

            // Σε πακέτα SQL Batch, το ερώτημα ξεκινά μετά από 8 bytes επικεφαλίδα + 4 bytes headers = 12 bytes
            if (packet.Length > 12)
            {
                byte[] queryBytes = new byte[packet.Length - 12];
                Array.Copy(packet, 12, queryBytes, 0, queryBytes.Length);

                // Το κείμενο είναι σε Unicode (UTF-16LE)
                string sqlQuery = Encoding.Unicode.GetString(queryBytes).TrimEnd('\0');

                _analysisWriter.WriteLine("ΕΞΑΓΩΓΗ SQL QUERY:");
                _analysisWriter.WriteLine("------------------");
                _analysisWriter.WriteLine(sqlQuery);
                _analysisWriter.WriteLine("------------------");

                _analysisWriter.WriteLine("\nΑΝΑΛΥΣΗ ΔΟΜΗΣ ΠΑΚΕΤΟΥ SQL BATCH:");
                _analysisWriter.WriteLine("Bytes 0-7: TDS Header");
                _analysisWriter.WriteLine("Bytes 8-11: Transaction Descriptor & Headers");
                _analysisWriter.WriteLine("Bytes 12+: SQL Query Text (Unicode UTF-16LE)");

                _analysisWriter.WriteLine("\nΚΩΔΙΚΑΣ ΕΞΑΓΩΓΗΣ ΓΙΑ TDS LISTENER:");
                _analysisWriter.WriteLine("if (packetType == 0x01 && packet.Length > 12) {");
                _analysisWriter.WriteLine("    byte[] queryBytes = new byte[packet.Length - 12];");
                _analysisWriter.WriteLine("    Array.Copy(packet, 12, queryBytes, 0, queryBytes.Length);");
                _analysisWriter.WriteLine("    string sqlQuery = Encoding.Unicode.GetString(queryBytes).TrimEnd('\\0');");
                _analysisWriter.WriteLine("    // Process SQL query...");
                _analysisWriter.WriteLine("}");
            }
        }

        private void AnalyzeRpcPacket(byte[] packet)
        {
            _analysisWriter.WriteLine("\n=== RPC PACKET ANALYSIS ===");

            // Σε πακέτα RPC, μετά την επικεφαλίδα 8 bytes + 4 bytes headers = 12 bytes
            // ακολουθούν πληροφορίες για το procedure
            if (packet.Length > 12)
            {
                _analysisWriter.WriteLine("\nΑΝΑΛΥΣΗ ΔΟΜΗΣ ΠΑΚΕΤΟΥ RPC:");
                _analysisWriter.WriteLine("Bytes 0-7: TDS Header");
                _analysisWriter.WriteLine("Bytes 8-11: Transaction Descriptor & Headers");

                if (packet.Length > 14)
                {
                    // Μέγεθος ονόματος procedure (2 bytes)
                    ushort procNameLength = BitConverter.ToUInt16(packet, 12);
                    _analysisWriter.WriteLine($"Bytes 12-13: Procedure Name Length ({procNameLength})");

                    if (procNameLength > 0 && packet.Length >= 14 + procNameLength)
                    {
                        byte[] procNameBytes = new byte[procNameLength];
                        Array.Copy(packet, 14, procNameBytes, 0, procNameLength);

                        // Το όνομα είναι σε ASCII ή Unicode ανάλογα με τις ρυθμίσεις
                        string procName = Encoding.ASCII.GetString(procNameBytes);
                        _analysisWriter.WriteLine($"Bytes 14-{14 + procNameLength - 1}: Procedure Name ({procName})");

                        _analysisWriter.WriteLine("\nΕΞΑΓΩΓΗ RPC CALL:");
                        _analysisWriter.WriteLine("----------------");
                        _analysisWriter.WriteLine($"Procedure: {procName}");
                        _analysisWriter.WriteLine("----------------");

                        // Μετά το όνομα ακολουθούν options και παράμετροι
                        _analysisWriter.WriteLine($"\nBytes {14 + procNameLength}+: Options & Parameters");

                        _analysisWriter.WriteLine("\nΚΩΔΙΚΑΣ ΕΞΑΓΩΓΗΣ ΓΙΑ TDS LISTENER:");
                        _analysisWriter.WriteLine("if (packetType == 0x03 && packet.Length > 14) {");
                        _analysisWriter.WriteLine("    ushort procNameLength = BitConverter.ToUInt16(packet, 12);");
                        _analysisWriter.WriteLine("    if (procNameLength > 0 && packet.Length >= 14 + procNameLength) {");
                        _analysisWriter.WriteLine("        byte[] procNameBytes = new byte[procNameLength];");
                        _analysisWriter.WriteLine("        Array.Copy(packet, 14, procNameBytes, 0, procNameLength);");
                        _analysisWriter.WriteLine("        string procName = Encoding.ASCII.GetString(procNameBytes);");
                        _analysisWriter.WriteLine("        // Process RPC call to procedure: procName");
                        _analysisWriter.WriteLine("    }");
                        _analysisWriter.WriteLine("}");
                    }
                }
            }
        }

        private void AnalyzeResultPacket(byte[] packet)
        {
            _analysisWriter.WriteLine("\n=== TABULAR RESULT PACKET ANALYSIS ===");

            if (packet.Length > 8)
            {
                _analysisWriter.WriteLine("\nΑΝΑΛΥΣΗ ΔΟΜΗΣ ΠΑΚΕΤΟΥ ΑΠΟΤΕΛΕΣΜΑΤΩΝ:");
                _analysisWriter.WriteLine("Bytes 0-7: TDS Header");

                // Ανάλυση tokens
                int position = 8;
                while (position < packet.Length)
                {
                    byte tokenType = packet[position++];
                    string tokenName = GetTokenName(tokenType);

                    _analysisWriter.WriteLine($"\nToken at position {position - 1}: 0x{tokenType:X2} ({tokenName})");

                    // Ανάλογα με τον τύπο token, έχουμε διαφορετική δομή
                    switch (tokenType)
                    {
                        case 0x81: // COLMETADATA
                            if (position + 1 < packet.Length)
                            {
                                ushort columnCount = packet[position];
                                _analysisWriter.WriteLine($"  Column Count: {columnCount}");
                                // Η πλήρης ανάλυση του COLMETADATA είναι πολύπλοκη
                            }
                            break;

                        case 0xD1: // ROW
                            _analysisWriter.WriteLine("  Row Data follows");
                            break;

                        case 0xFD: // DONE
                        case 0xFE: // DONEPROC
                        case 0xFF: // DONEINPROC
                            if (position + 8 <= packet.Length)
                            {
                                ushort status = BitConverter.ToUInt16(packet, position);
                                position += 2;
                                ushort currentCommand = BitConverter.ToUInt16(packet, position);
                                position += 2;
                                uint rowCount = BitConverter.ToUInt32(packet, position);
                                position += 4;

                                _analysisWriter.WriteLine($"  Status: 0x{status:X4}");
                                _analysisWriter.WriteLine($"  Current Command: {currentCommand}");
                                _analysisWriter.WriteLine($"  Row Count: {rowCount}");
                            }
                            break;

                        default:
                            // Για άλλα tokens, απλώς προχωράμε
                            position = packet.Length; // Skip
                            break;
                    }
                }

                _analysisWriter.WriteLine("\nΚΩΔΙΚΑΣ ΠΡΟΣΟΜΟΙΩΣΗΣ ΑΠΑΝΤΗΣΗΣ ΓΙΑ TDS LISTENER:");
                _analysisWriter.WriteLine("// Για έναν απλό TDS listener, μπορείτε να στείλετε μια προσομοιωμένη απάντηση");
                _analysisWriter.WriteLine("// με τα ακόλουθα tokens: COLMETADATA, ROW(s), DONE");
            }
        }

        private void AnalyzePreLoginPacket(byte[] packet)
        {
            _analysisWriter.WriteLine("\n=== PRELOGIN PACKET ANALYSIS ===");

            if (packet.Length > 8)
            {
                _analysisWriter.WriteLine("\nΑΝΑΛΥΣΗ ΔΟΜΗΣ ΠΑΚΕΤΟΥ PRELOGIN:");
                _analysisWriter.WriteLine("Bytes 0-7: TDS Header");

                int position = 8;
                while (position < packet.Length - 5 && packet[position] != 0xFF) // 0xFF είναι το terminator token
                {
                    byte optionToken = packet[position++];
                    string tokenName = GetPreLoginOptionName(optionToken);

                    if (position + 4 <= packet.Length)
                    {
                        ushort optionOffset = BitConverter.ToUInt16(packet, position);
                        position += 2;
                        ushort optionLength = BitConverter.ToUInt16(packet, position);
                        position += 2;

                        _analysisWriter.WriteLine($"\nOption: 0x{optionToken:X2} ({tokenName})");
                        _analysisWriter.WriteLine($"  Offset: {optionOffset}");
                        _analysisWriter.WriteLine($"  Length: {optionLength}");

                        // Λήψη της τιμής του option (εφόσον υπάρχει)
                        if (optionLength > 0 && 8 + optionOffset + optionLength <= packet.Length)
                        {
                            StringBuilder optionValueHex = new StringBuilder();
                            for (int i = 0; i < optionLength; i++)
                            {
                                optionValueHex.Append($"{packet[8 + optionOffset + i]:X2} ");
                            }

                            _analysisWriter.WriteLine($"  Value (hex): {optionValueHex}");

                            // Ειδική ανάλυση για συγκεκριμένα options
                            if (optionToken == 0x00 && optionLength >= 6) // VERSION
                            {
                                byte major = packet[8 + optionOffset];
                                byte minor = packet[8 + optionOffset + 1];
                                ushort build = BitConverter.ToUInt16(packet, 8 + optionOffset + 2);
                                ushort subbuild = BitConverter.ToUInt16(packet, 8 + optionOffset + 4);

                                _analysisWriter.WriteLine($"  Version: {major}.{minor}.{build}.{subbuild}");
                            }
                            else if (optionToken == 0x01 && optionLength >= 1) // ENCRYPTION
                            {
                                byte encOption = packet[8 + optionOffset];
                                string encName = "Unknown";
                                switch (encOption)
                                {
                                    case 0: encName = "ENCRYPT_OFF"; break;
                                    case 1: encName = "ENCRYPT_ON"; break;
                                    case 2: encName = "ENCRYPT_NOT_SUP"; break;
                                    case 3: encName = "ENCRYPT_REQ"; break;
                                    default: encName = $"UNKNOWN(0x{encOption:X2})"; break;
                                }

                                _analysisWriter.WriteLine($"  Encryption: {encName}");
                            }
                        }
                    }
                }

                if (position < packet.Length && packet[position] == 0xFF)
                {
                    _analysisWriter.WriteLine("\nTerminator token found at position " + position);
                }

                _analysisWriter.WriteLine("\nΚΩΔΙΚΑΣ ΓΙΑ TDS LISTENER (PRELOGIN RESPONSE):");
                _analysisWriter.WriteLine("// Για έναν απλό TDS listener, πρέπει να απαντήσετε στο PreLogin");
                _analysisWriter.WriteLine("// με τις ελάχιστες απαιτούμενες πληροφορίες: VERSION, ENCRYPTION, κλπ.");
            }
        }

        private void AnalyzeLoginPacket(byte[] packet)
        {
            _analysisWriter.WriteLine("\n=== LOGIN PACKET ANALYSIS ===");

            if (packet.Length > 36)
            {
                _analysisWriter.WriteLine("\nΑΝΑΛΥΣΗ ΔΟΜΗΣ ΠΑΚΕΤΟΥ LOGIN:");
                _analysisWriter.WriteLine("Bytes 0-7: TDS Header");

                uint length = BitConverter.ToUInt32(packet, 8);
                _analysisWriter.WriteLine($"Bytes 8-11: Length ({length})");

                uint tdsVersion = BitConverter.ToUInt32(packet, 12);
                _analysisWriter.WriteLine($"Bytes 12-15: TDS Version (0x{tdsVersion:X8})");

                uint packetSize = BitConverter.ToUInt32(packet, 16);
                _analysisWriter.WriteLine($"Bytes 16-19: Packet Size ({packetSize})");

                uint clientVersion = BitConverter.ToUInt32(packet, 20);
                _analysisWriter.WriteLine($"Bytes 20-23: Client Version (0x{clientVersion:X8})");

                uint clientPid = BitConverter.ToUInt32(packet, 24);
                _analysisWriter.WriteLine($"Bytes 24-27: Client PID ({clientPid})");

                uint connectionId = BitConverter.ToUInt32(packet, 28);
                _analysisWriter.WriteLine($"Bytes 28-31: Connection ID ({connectionId})");

                // Option Flags 1
                byte optionFlags1 = packet[32];
                _analysisWriter.WriteLine($"Byte 32: Option Flags 1 (0x{optionFlags1:X2})");

                // Option Flags 2
                byte optionFlags2 = packet[33];
                _analysisWriter.WriteLine($"Byte 33: Option Flags 2 (0x{optionFlags2:X2})");

                // Υπόλοιπες πληροφορίες Login...

                _analysisWriter.WriteLine("\nΚΩΔΙΚΑΣ ΓΙΑ TDS LISTENER (LOGIN RESPONSE):");
                _analysisWriter.WriteLine("// Για έναν απλό TDS listener, πρέπει να απαντήσετε με LOGINACK");
                _analysisWriter.WriteLine("// για να επιβεβαιώσετε τη σύνδεση και ENVCHANGE για τη βάση δεδομένων");
            }
        }

        private string GetPreLoginOptionName(byte option)
        {
            switch (option)
            {
                case 0x00: return "VERSION";
                case 0x01: return "ENCRYPTION";
                case 0x02: return "INSTOPT";
                case 0x03: return "THREADID";
                case 0x04: return "MARS";
                case 0x05: return "TRACEID";
                case 0x06: return "FEDAUTHREQUIRED";
                case 0x07: return "NONCEOPT";
                case 0xFF: return "TERMINATOR";
                default: return $"UNKNOWN(0x{option:X2})";
            }
        }

        private string GetTokenName(byte token)
        {
            switch (token)
            {
                case 0x81: return "COLMETADATA";
                case 0xA5: return "COLINFO";
                case 0xD1: return "ROW";
                case 0xD3: return "NBCROW";
                case 0xE3: return "ENVCHANGE";
                case 0xE5: return "ERROR";
                case 0xAB: return "INFO";
                case 0xAD: return "LOGINACK";
                case 0xFD: return "DONE";
                case 0xFE: return "DONEPROC";
                case 0xFF: return "DONEINPROC";
                default: return $"UNKNOWN(0x{token:X2})";
            }
        }

        /// <summary>
        /// Σταματάει την καταγραφή των πακέτων TDS
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;

            // Γράψιμο επικεφαλίδας τέλους στα αρχεία
            byte[] footer = Encoding.ASCII.GetBytes("\r\nEND_OF_TDS_CAPTURE\r\n");
            _rawDataStream.Write(footer, 0, footer.Length);
            _rawDataStream.Flush();

            _hexDumpWriter.WriteLine("\n// === END OF TDS HEX DUMP ===");
            _hexDumpWriter.WriteLine($"// Total packets captured: {_packetCount}");
            _hexDumpWriter.WriteLine($"// End time: {DateTime.Now}");
            _hexDumpWriter.Flush();

            _analysisWriter.WriteLine("\n\n=== END OF TDS PROTOCOL ANALYSIS ===");
            _analysisWriter.WriteLine($"Total packets captured: {_packetCount}");
            _analysisWriter.WriteLine($"End time: {DateTime.Now}");
            _analysisWriter.Flush();

            Console.WriteLine($"Η καταγραφή τερματίστηκε. Συνολικά καταγράφηκαν {_packetCount} πακέτα TDS.");
        }

        /// <summary>
        /// Απελευθερώνει τους πόρους
        /// </summary>
        public void Dispose()
        {
            Stop();

            _tcpProxy?.Dispose();
        }


    }

    /// <summary>
    /// Κλάση για υποκατάσταση του Console.Out και ανάλυση των TDS πακέτων
    /// </summary>
    public class TdsTraceInterceptor : TextWriter
    {
        private TextWriter _originalWriter;
        private TdsPacketCapture _packetCapture;
        private StringBuilder _lineBuffer = new StringBuilder();

        public TdsTraceInterceptor(TextWriter originalWriter, TdsPacketCapture packetCapture)
        {
            _originalWriter = originalWriter;
            _packetCapture = packetCapture;
        }

        public override Encoding Encoding => _originalWriter.Encoding;

        public override void Write(char value)
        {
            // Προώθηση στο original writer
            _originalWriter.Write(value);

            // Συλλογή των χαρακτήρων για ανάλυση
            if (value == '\n')
            {
                string line = _lineBuffer.ToString().Trim();
                _lineBuffer.Clear();

                // Ανάλυση της γραμμής για TDS πληροφορίες
                AnalyzeTraceLine(line);
            }
            else if (value != '\r')
            {
                _lineBuffer.Append(value);
            }
        }

        private void AnalyzeTraceLine(string line)
        {
            try
            {
                // Εξαγωγή πληροφοριών TDS από τη γραμμή trace
                if (line.Contains("Sending") && (line.Contains("bytes") || line.Contains("packet")))
                {
                    // Προσπάθεια εξαγωγής hex dump από το trace (αν υπάρχει)
                    ExtractHexDumpFromTrace(line, "CLIENT_TO_SERVER");
                }
                else if (line.Contains("Received") && (line.Contains("bytes") || line.Contains("packet")))
                {
                    ExtractHexDumpFromTrace(line, "SERVER_TO_CLIENT");
                }
            }
            catch (Exception ex)
            {
                _originalWriter.WriteLine($"Error analyzing trace line: {ex.Message}");
            }
        }

        private void ExtractHexDumpFromTrace(string line, string direction)
        {
            // Έλεγχος για hex dump στο trace output
            if (line.Contains("0x") && line.Count(c => c == ' ') > 10)
            {
                // Καταγραφή του γεγονότος
                _packetCapture.LogEvent("TRACE_HEX_DUMP", $"{direction}: {line}");

                // Έλεγχος αν το hex dump είναι σε format που μπορούμε να αναλύσουμε
                int hexIndex = line.IndexOf("0x");
                if (hexIndex >= 0)
                {
                    try
                    {
                        // Εξαγωγή των hex bytes
                        List<byte> bytes = new List<byte>();
                        string hexString = line.Substring(hexIndex);
                        string[] hexTokens = hexString.Split(' ', '\t');

                        foreach (string token in hexTokens)
                        {
                            if (token.StartsWith("0x") && token.Length >= 4)
                            {
                                byte value = Convert.ToByte(token.Substring(2), 16);
                                bytes.Add(value);
                            }
                        }

                        // Αν έχουμε αρκετά bytes για επικεφαλίδα TDS, τα καταγράφουμε
                        if (bytes.Count >= 8)
                        {
                            _packetCapture.CapturePacket(bytes.ToArray(), direction);
                        }
                    }
                    catch
                    {
                        // Αγνοούμε errors στην εξαγωγή hex dump
                    }
                }
            }
        }
    }

    /// <summary>
    /// Κλάση TCP proxy (stub - δεν υλοποιείται πλήρως σε αυτό το παράδειγμα)
    /// </summary>
    public class TcpProxy : IDisposable
    {
        // Στην πραγματικότητα, θα υλοποιούσαμε έναν πραγματικό TCP proxy
        // που θα μεσολαβούσε στην επικοινωνία SQL client - SQL server

        public void Dispose()
        {
            // Cleanup
        }
    }
    #endregion
}