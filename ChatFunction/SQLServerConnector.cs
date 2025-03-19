using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChatFunction
{
    class SQLServerConnector
    {
        
        private readonly string _connectionString = Environment.GetEnvironmentVariable("DefaultConnection");

        public string ExecuteQuery(string query)
        {
            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        StringBuilder result = new StringBuilder();
                        while (reader.Read())
                        {
                            result.Append(reader[0].ToString()).Append(" ");
                        }
                        return result.ToString().Trim();
                    }
                }
            }
        }
    }
}
