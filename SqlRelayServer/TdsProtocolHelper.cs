using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace SqlRelayServer
{
    // Αυτή η κλάση περιέχει λειτουργίες για κωδικοποίηση/αποκωδικοποίηση TDS πακέτων
    public static class TdsProtocolHelper
    {
        // Constants for TDS packet types
        private const byte TDS_TYPE_SQL_BATCH = 0x01;
        private const byte TDS_TYPE_PRE_LOGIN = 0x12;
        private const byte TDS_TYPE_TDS7_LOGIN = 0x10;
        private const byte TDS_TYPE_TABULAR_RESULT = 0x04;
        private const byte TDS_TYPE_ERROR = 0xAA;

        private const byte TDS_STATUS_EOM = 0x01; // End of Message

        // Αποκωδικοποίηση ενός TDS πακέτου και εξαγωγή του SQL ερωτήματος
        public static string DecodeTdsPacket(byte[] packet)
        {
            if (packet == null || packet.Length < 8)
            {
                return null; // Πολύ μικρό για να είναι έγκυρο TDS πακέτο
            }

            // Εξαγωγή επικεφαλίδας
            byte packetType = packet[0];
            byte status = packet[1];
            ushort length = BitConverter.ToUInt16(packet, 2);
            ushort spid = BitConverter.ToUInt16(packet, 4);
            byte packetId = packet[6];

            // Έλεγχος εάν είναι SQL Batch πακέτο
            if (packetType == TDS_TYPE_SQL_BATCH)
            {
                // Εξαγωγή του SQL ερωτήματος (ξεκινά μετά την επικεφαλίδα των 8 bytes)
                // Σε μια πραγματική υλοποίηση, θα χρειαζόταν περισσότερη ανάλυση για διαφορετικές κωδικοποιήσεις
                return Encoding.Unicode.GetString(packet, 8, packet.Length - 8);
            }
            else if (packetType == TDS_TYPE_PRE_LOGIN)
            {
                // Αποστολή προκαθορισμένης απάντησης για PreLogin
                return "PRELOGIN"; // Ειδικός κωδικός για προσομοίωση του PreLogin
            }
            else if (packetType == TDS_TYPE_TDS7_LOGIN)
            {
                // Αποστολή προκαθορισμένης απάντησης για Login
                return "LOGIN"; // Ειδικός κωδικός για προσομοίωση του Login
            }

            return null; // Άγνωστος τύπος πακέτου
        }

        // Δημιουργία απάντησης PreLogin
        public static byte[] CreatePreLoginResponse()
        {
            // Απλοποιημένη προσομοίωση της απάντησης PreLogin
            // Σε πραγματική υλοποίηση, θα χρειαζόταν σωστή μορφοποίηση κατά το πρωτόκολλο TDS
            byte[] response = new byte[32];
            response[0] = TDS_TYPE_PRE_LOGIN; // Τύπος πακέτου
            response[1] = TDS_STATUS_EOM;     // Κατάσταση πακέτου (End of Message)
            BitConverter.GetBytes((ushort)32).CopyTo(response, 2); // Μήκος πακέτου
            BitConverter.GetBytes((ushort)1).CopyTo(response, 4);  // SPID = 1
            response[6] = 1; // Packet ID

            // Υπόλοιπο πακέτο με προκαθορισμένες τιμές
            return response;
        }

        // Δημιουργία απάντησης Login
        public static byte[] CreateLoginResponse()
        {
            // Απλοποιημένη προσομοίωση της απάντησης Login
            byte[] response = new byte[32];
            response[0] = TDS_TYPE_TDS7_LOGIN; // Τύπος πακέτου
            response[1] = TDS_STATUS_EOM;      // Κατάσταση πακέτου (End of Message)
            BitConverter.GetBytes((ushort)32).CopyTo(response, 2); // Μήκος πακέτου
            BitConverter.GetBytes((ushort)1).CopyTo(response, 4);  // SPID = 1
            response[6] = 1; // Packet ID

            // Υπόλοιπο πακέτο με προκαθορισμένες τιμές επιτυχούς σύνδεσης
            return response;
        }

        // Μετατροπή του JSON αποτελέσματος σε TDS απάντηση
        public static byte[] EncodeTdsResult(string jsonResult)
        {
            try
            {
                // Αποκωδικοποίηση του JSON σε DataTable
                DataTable dataTable = JsonConvert.DeserializeObject<DataTable>(jsonResult);

                if (dataTable == null)
                {
                    // Σε περίπτωση που το JSON δεν μπορεί να μετατραπεί σε DataTable
                    return EncodeTdsError("Invalid result format");
                }

                // Υπολογισμός μεγέθους πακέτου (απλοποιημένος)
                int headerSize = 8;
                int colDescSize = 20 * dataTable.Columns.Count; // ~20 bytes ανά περιγραφή στήλης
                int rowsSize = EstimateRowsSize(dataTable);     // Εκτίμηση μεγέθους δεδομένων γραμμών
                int totalSize = headerSize + colDescSize + rowsSize;

                byte[] response = new byte[totalSize];

                // Συμπλήρωση επικεφαλίδας
                response[0] = TDS_TYPE_TABULAR_RESULT; // Τύπος πακέτου
                response[1] = TDS_STATUS_EOM;          // Κατάσταση πακέτου (End of Message)
                BitConverter.GetBytes((ushort)totalSize).CopyTo(response, 2); // Μήκος πακέτου
                BitConverter.GetBytes((ushort)1).CopyTo(response, 4);         // SPID = 1
                response[6] = 1; // Packet ID

                // ΣΗΜΕΙΩΣΗ: Σε μια πραγματική υλοποίηση, εδώ θα συμπληρώναμε λεπτομερώς 
                // τις περιγραφές στηλών και τα δεδομένα γραμμών σύμφωνα με το πρωτόκολλο TDS

                return response;
            }
            catch (Exception ex)
            {
                return EncodeTdsError($"Error encoding result: {ex.Message}");
            }
        }

        // Δημιουργία μηνύματος σφάλματος σε μορφή TDS
        public static byte[] EncodeTdsError(string errorMessage)
        {
            // Υπολογισμός μεγέθους πακέτου
            int headerSize = 8;
            int errorMsgSize = Encoding.Unicode.GetByteCount(errorMessage);
            int totalSize = headerSize + 20 + errorMsgSize; // 20 bytes για επιπλέον πληροφορίες σφάλματος

            byte[] response = new byte[totalSize];

            // Συμπλήρωση επικεφαλίδας
            response[0] = TDS_TYPE_ERROR; // Τύπος πακέτου
            response[1] = TDS_STATUS_EOM; // Κατάσταση πακέτου (End of Message)
            BitConverter.GetBytes((ushort)totalSize).CopyTo(response, 2); // Μήκος πακέτου
            BitConverter.GetBytes((ushort)1).CopyTo(response, 4);         // SPID = 1
            response[6] = 1; // Packet ID

            // Συμπλήρωση κωδικού σφάλματος (τυχαίος αριθμός για παράδειγμα)
            BitConverter.GetBytes((int)50000).CopyTo(response, 8); // Κωδικός σφάλματος

            // Συμπλήρωση μηνύματος σφάλματος (απλοποιημένο)
            byte[] msgBytes = Encoding.Unicode.GetBytes(errorMessage);
            Array.Copy(msgBytes, 0, response, 28, msgBytes.Length);

            return response;
        }

        // Βοηθητική μέθοδος για εκτίμηση μεγέθους δεδομένων γραμμών
        private static int EstimateRowsSize(DataTable dataTable)
        {
            int totalSize = 0;

            foreach (DataRow row in dataTable.Rows)
            {
                foreach (DataColumn col in dataTable.Columns)
                {
                    if (row[col] == DBNull.Value)
                    {
                        totalSize += 1; // Null marker
                    }
                    else
                    {
                        object value = row[col];
                        Type type = col.DataType;

                        if (type == typeof(string))
                        {
                            string str = (string)value;
                            totalSize += 2 + (str.Length * 2); // 2 bytes μήκος + unicode χαρακτήρες
                        }
                        else if (type == typeof(int) || type == typeof(long))
                        {
                            totalSize += 8; // Int/long max 8 bytes
                        }
                        else if (type == typeof(double) || type == typeof(decimal))
                        {
                            totalSize += 8; // Double/decimal max 8 bytes
                        }
                        else if (type == typeof(DateTime))
                        {
                            totalSize += 8; // DateTime max 8 bytes
                        }
                        else if (type == typeof(bool))
                        {
                            totalSize += 1; // Boolean max 1 byte
                        }
                        else
                        {
                            totalSize += 16; // Άλλοι τύποι - προεπιλογή
                        }
                    }
                }

                totalSize += 4; // Overhead ανά γραμμή
            }

            return totalSize;
        }

        // Μέθοδος για καθαρισμό του SQL ερωτήματος (αφαίρεση σχολίων, normalization)
        public static string SanitizeSqlQuery(string query)
        {
            if (string.IsNullOrEmpty(query))
                return query;

            // Αφαίρεση σχολίων
            query = Regex.Replace(query, @"--.*$", "", RegexOptions.Multiline);
            query = Regex.Replace(query, @"/\*.*?\*/", "", RegexOptions.Singleline);

            // Αντικατάσταση πολλαπλών κενών με ένα
            query = Regex.Replace(query, @"\s+", " ");

            // Αφαίρεση κενών στην αρχή και το τέλος
            query = query.Trim();

            return query;
        }
    }
}