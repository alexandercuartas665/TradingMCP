' ============================================================
' HipotesisRepository.vb
'
' Registro de hipotesis: cada idea se apunta (con su motivo) ANTES
' de probarla y se guarda su resultado. El numero de hipotesis
' probadas sirve para corregir los veredictos por pruebas
' multiples: con muchas pruebas, algunas salen bien por azar.
' ============================================================
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq
Imports Npgsql
Imports NpgsqlTypes

Namespace WpfDerivClientVB.Backtesting

    Public Class HipotesisRepository

        Private ReadOnly _connectionString As String

        Public Sub New()
            _connectionString = ConfigManager.LoadDbConfig().GetConnectionString()
        End Sub

        Public Async Function EnsureTableAsync() As Task
            Using conn As New NpgsqlConnection(_connectionString)
                Await conn.OpenAsync()
                Using cmd As New NpgsqlCommand(
                    "CREATE TABLE IF NOT EXISTS public.hipotesis (" &
                    "  id                  SERIAL PRIMARY KEY," &
                    "  creada_utc          TIMESTAMPTZ NOT NULL DEFAULT now()," &
                    "  numero_prueba       INTEGER     NOT NULL," &
                    "  estrategia          VARCHAR(80) NOT NULL," &
                    "  symbol              VARCHAR(30) NOT NULL," &
                    "  timeframe           VARCHAR(10) NOT NULL," &
                    "  duracion            INTEGER     NOT NULL," &
                    "  confianza           INTEGER     NOT NULL," &
                    "  payout              NUMERIC(6,2) NOT NULL," &
                    "  corte_epoch         BIGINT      NOT NULL," &
                    "  motivo              TEXT        NOT NULL," &
                    "  estado              VARCHAR(20) NOT NULL DEFAULT 'registrada'," &
                    "  veredicto_ciego     VARCHAR(40)," &
                    "  veredicto_corregido VARCHAR(40)," &
                    "  conclusion          TEXT," &
                    "  resultado           JSONB" &
                    ");", conn)
                    Await cmd.ExecuteNonQueryAsync()
                End Using
            End Using
        End Function

        ''' <summary>Registra la hipotesis y devuelve (id, numero de prueba, veces que ya se probo esa misma combinacion).</summary>
        Public Async Function RegistrarAsync(estrategia As String, symbol As String, timeframe As String, duracion As Integer,
                                             confianza As Integer, payout As Decimal, corteEpoch As Long,
                                             motivo As String) As Task(Of Tuple(Of Integer, Integer, Integer))
            Await EnsureTableAsync()
            Using conn As New NpgsqlConnection(_connectionString)
                Await conn.OpenAsync()
                Dim repetidas As Integer
                Using cmd As New NpgsqlCommand(
                    "SELECT COUNT(*) FROM public.hipotesis WHERE estrategia = @e AND symbol = @s AND timeframe = @t " &
                    "AND duracion = @d AND confianza = @c;", conn)
                    cmd.Parameters.AddWithValue("e", estrategia) : cmd.Parameters.AddWithValue("s", symbol)
                    cmd.Parameters.AddWithValue("t", timeframe) : cmd.Parameters.AddWithValue("d", duracion)
                    cmd.Parameters.AddWithValue("c", confianza)
                    repetidas = CInt(CLng(Await cmd.ExecuteScalarAsync()))
                End Using
                Using cmd As New NpgsqlCommand(
                    "INSERT INTO public.hipotesis (numero_prueba, estrategia, symbol, timeframe, duracion, confianza, payout, corte_epoch, motivo) " &
                    "VALUES ((SELECT COUNT(*) + 1 FROM public.hipotesis), @e, @s, @t, @d, @c, @p, @k, @m) " &
                    "RETURNING id, numero_prueba;", conn)
                    cmd.Parameters.AddWithValue("e", estrategia) : cmd.Parameters.AddWithValue("s", symbol)
                    cmd.Parameters.AddWithValue("t", timeframe) : cmd.Parameters.AddWithValue("d", duracion)
                    cmd.Parameters.AddWithValue("c", confianza) : cmd.Parameters.AddWithValue("p", payout)
                    cmd.Parameters.AddWithValue("k", corteEpoch) : cmd.Parameters.AddWithValue("m", motivo)
                    Using rd = Await cmd.ExecuteReaderAsync()
                        Await rd.ReadAsync()
                        Return Tuple.Create(rd.GetInt32(0), rd.GetInt32(1), repetidas)
                    End Using
                End Using
            End Using
        End Function

        Public Async Function GuardarResultadoAsync(id As Integer, estado As String, veredictoCiego As String,
                                                    veredictoCorregido As String, conclusion As String, resultado As JObject) As Task
            Using conn As New NpgsqlConnection(_connectionString)
                Await conn.OpenAsync()
                Using cmd As New NpgsqlCommand(
                    "UPDATE public.hipotesis SET estado = @st, veredicto_ciego = @v, veredicto_corregido = @vc, " &
                    "conclusion = @co, resultado = @r WHERE id = @id;", conn)
                    cmd.Parameters.AddWithValue("st", estado)
                    cmd.Parameters.AddWithValue("v", If(veredictoCiego, ""))
                    cmd.Parameters.AddWithValue("vc", If(veredictoCorregido, ""))
                    cmd.Parameters.AddWithValue("co", If(conclusion, ""))
                    cmd.Parameters.Add(New NpgsqlParameter("r", NpgsqlDbType.Jsonb) With {
                        .Value = If(resultado Is Nothing, "{}", resultado.ToString(Newtonsoft.Json.Formatting.None))})
                    cmd.Parameters.AddWithValue("id", id)
                    Await cmd.ExecuteNonQueryAsync()
                End Using
            End Using
        End Function

        ''' <summary>Ultimas hipotesis registradas (sin el JSON completo del resultado).</summary>
        Public Async Function ListarAsync(Optional limite As Integer = 100) As Task(Of JArray)
            Await EnsureTableAsync()
            Dim res As New JArray()
            Using conn As New NpgsqlConnection(_connectionString)
                Await conn.OpenAsync()
                Using cmd As New NpgsqlCommand(
                    "SELECT id, numero_prueba, creada_utc, estrategia, symbol, timeframe, duracion, confianza, payout, " &
                    "motivo, estado, COALESCE(veredicto_ciego,''), COALESCE(veredicto_corregido,''), COALESCE(conclusion,'') " &
                    "FROM public.hipotesis ORDER BY id DESC LIMIT @l;", conn)
                    cmd.Parameters.AddWithValue("l", limite)
                    Using rd = Await cmd.ExecuteReaderAsync()
                        While Await rd.ReadAsync()
                            res.Add(New JObject From {
                                {"id", rd.GetInt32(0)}, {"numero_prueba", rd.GetInt32(1)},
                                {"creada_utc", rd.GetDateTime(2).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")},
                                {"estrategia", rd.GetString(3)}, {"symbol", rd.GetString(4)}, {"timeframe", rd.GetString(5)},
                                {"duracion", rd.GetInt32(6)}, {"confianza", rd.GetInt32(7)}, {"payout", rd.GetDecimal(8)},
                                {"motivo", rd.GetString(9)}, {"estado", rd.GetString(10)},
                                {"veredicto_ciego", rd.GetString(11)}, {"veredicto_corregido", rd.GetString(12)},
                                {"conclusion", rd.GetString(13)}
                            })
                        End While
                    End Using
                End Using
            End Using
            Return res
        End Function

    End Class

End Namespace
