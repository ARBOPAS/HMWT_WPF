using Microsoft.Data.Sqlite;
using System;

namespace HMWT_MUI.Services
{
    public class CharacterMoney
    {
        public int Platinum { get; set; }
        public int Electrum { get; set; }
        public int Gold { get; set; }
        public int Silver { get; set; }
        public int Copper { get; set; }

        public decimal TotalGoldValue => Gold + Platinum * 10m + Electrum * 0.5m + Silver * 0.1m + Copper * 0.01m;

        /// Дельта по монете, не ниже нуля
        public void Apply(string currency, int delta)
        {
            switch (currency)
            {
                case "Platinum": Platinum = Math.Max(0, Platinum + delta); break;
                case "Electrum": Electrum = Math.Max(0, Electrum + delta); break;
                case "Gold": Gold = Math.Max(0, Gold + delta); break;
                case "Silver": Silver = Math.Max(0, Silver + delta); break;
                case "Copper": Copper = Math.Max(0, Copper + delta); break;
            }
        }
    }

    public class MoneyService
    {
        private readonly int _characterId;
        public MoneyService(int characterId) => _characterId = characterId;

        public CharacterMoney LoadMoney()
        {
            var money = new CharacterMoney();
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(
                "SELECT Platinum, Electrum, Gold, Silver, Copper FROM GoldTable WHERE id_character=@id", conn);
            cmd.Parameters.AddWithValue("@id", _characterId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                money.Platinum = Convert.ToInt32(reader["Platinum"]);
                money.Electrum = Convert.ToInt32(reader["Electrum"]);
                money.Gold = Convert.ToInt32(reader["Gold"]);
                money.Silver = Convert.ToInt32(reader["Silver"]);
                money.Copper = Convert.ToInt32(reader["Copper"]);
            }
            return money;
        }

        public void SaveMoney(CharacterMoney money)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var check = new SqliteCommand("SELECT COUNT(*) FROM GoldTable WHERE id_character=@id", conn);
            check.Parameters.AddWithValue("@id", _characterId);
            bool exists = Convert.ToInt32(check.ExecuteScalar()) > 0;

            using var cmd = new SqliteCommand(
                exists
                    ? "UPDATE GoldTable SET Platinum=@p, Electrum=@e, Gold=@g, Silver=@s, Copper=@c WHERE id_character=@id"
                    : "INSERT INTO GoldTable (id_character, Platinum, Electrum, Gold, Silver, Copper) VALUES (@id,@p,@e,@g,@s,@c)",
                conn);
            cmd.Parameters.AddWithValue("@id", _characterId);
            cmd.Parameters.AddWithValue("@p", money.Platinum);
            cmd.Parameters.AddWithValue("@e", money.Electrum);
            cmd.Parameters.AddWithValue("@g", money.Gold);
            cmd.Parameters.AddWithValue("@s", money.Silver);
            cmd.Parameters.AddWithValue("@c", money.Copper);
            cmd.ExecuteNonQuery();
        }



    }
}