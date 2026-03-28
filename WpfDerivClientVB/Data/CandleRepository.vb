Imports System.Collections.Generic
Imports System.Threading.Tasks
Imports Npgsql

Namespace WpfDerivClientVB
    ''' <summary>
    ''' Servicio de persistencia de velas OHLC en PostgreSQL.
    ''' Independiente de WPF: puede ser usado por cualquier proyecto .NET.
    ''' La deduplicacion se delega al constraint UNIQUE de la BD.
    ''' </summary>
    Public Class CandleRepository

        Private ReadOnly _connectionString As String

        ''' <param name="host">Servidor (ej: localhost)</param>
        ''' <param name="port">Puerto host del contenedor (ej: 8083)</param>
        ''' <param name="database">Nombre de la BD (ej: traiding_db)</param>
        ''' <param name="username">Usuario PostgreSQL</param>
        ''' <param name="password">Contraseña PostgreSQL</param>
        Public Sub New(host As String, port As Integer, database As String,
                       username As String, password As String)
            _connectionString = String.Format(
                "Host={0};Port={1};Database={2};Username={3};Password={4};Pooling=true;Minimum Pool Size=1;Maximum Pool Size=10;",
                host, port, database, username, password)
        End Sub

        ''' <summary>
        ''' Guarda una sola vela. Si ya existe (mismo platform+symbol+tf+epoch), la ignora.
        ''' </summary>
        Public Async Function SaveCandleAsync(platform As String,
                                              symbol As String,
                                              timeframe As String,
                                              candle As CandleModel,
                                              isClosed As Boolean) As Task
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql As String =
                        "INSERT INTO public.candles " &
                        "  (platform, symbol, timeframe, epoch, open_time, open_price, high_price, low_price, close_price, is_closed) " &
                        "VALUES " &
                        "  (@platform, @symbol, @timeframe, @epoch, @open_time, @open_price, @high_price, @low_price, @close_price, @is_closed) " &
                        "ON CONFLICT (platform, symbol, timeframe, epoch) " &
                        "DO UPDATE SET " &
                        "  close_price = EXCLUDED.close_price, " &
                        "  high_price  = GREATEST(candles.high_price, EXCLUDED.high_price), " &
                        "  low_price   = LEAST(candles.low_price, EXCLUDED.low_price), " &
                        "  is_closed   = EXCLUDED.is_closed;"

                    Using cmd As New NpgsqlCommand(sql, conn)
                        AddCandleParams(cmd, platform, symbol, timeframe, candle, isClosed)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                End Using
            Catch ex As Exception
                Console.WriteLine("[CandleRepository.SaveCandleAsync] Error: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' Guarda un lote de velas de forma eficiente (historial inicial).
        ''' Omite duplicados silenciosamente via ON CONFLICT DO NOTHING.
        ''' </summary>
        Public Async Function SaveBatchAsync(platform As String,
                                             symbol As String,
                                             timeframe As String,
                                             candles As IEnumerable(Of CandleModel),
                                             isClosed As Boolean) As Task
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()

                    ' Usamos una transaccion para eficiencia en batch
                    Using tx = conn.BeginTransaction()
                        Dim sql As String =
                            "INSERT INTO public.candles " &
                            "  (platform, symbol, timeframe, epoch, open_time, open_price, high_price, low_price, close_price, is_closed) " &
                            "VALUES " &
                            "  (@platform, @symbol, @timeframe, @epoch, @open_time, @open_price, @high_price, @low_price, @close_price, @is_closed) " &
                            "ON CONFLICT (platform, symbol, timeframe, epoch) DO NOTHING;"

                        For Each candle In candles
                            Using cmd As New NpgsqlCommand(sql, conn, tx)
                                AddCandleParams(cmd, platform, symbol, timeframe, candle, isClosed)
                                Await cmd.ExecuteNonQueryAsync()
                            End Using
                        Next

                        tx.Commit()
                    End Using
                End Using
                Console.WriteLine(String.Format("[CandleRepository] Batch guardado: {0} {1} {2}", platform, symbol, timeframe))
            Catch ex As Exception
                Console.WriteLine("[CandleRepository.SaveBatchAsync] Error: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' Cuenta cuantas velas hay guardadas para un activo/temporalidad.
        ''' Util para verificar que la persistencia funciona.
        ''' </summary>
        Public Async Function CountAsync(platform As String,
                                         symbol As String,
                                         timeframe As String) As Task(Of Long)
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql As String =
                        "SELECT COUNT(*) FROM public.candles " &
                        "WHERE platform=@platform AND symbol=@symbol AND timeframe=@timeframe;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("platform", platform)
                        cmd.Parameters.AddWithValue("symbol", symbol)
                        cmd.Parameters.AddWithValue("timeframe", timeframe)
                        Return CLng(Await cmd.ExecuteScalarAsync())
                    End Using
                End Using
            Catch ex As Exception
                Console.WriteLine("[CandleRepository.CountAsync] Error: " & ex.Message)
                Return -1
            End Try
        End Function

        ''' <summary>
        ''' Carga velas históricas desde PostgreSQL para calcular indicadores.
        ''' Devuelve la lista ordenada de MAS ANTIGUA a MAS RECIENTE (oldest first),
        ''' que es el orden requerido por todos los calculadores de indicadores tecnicos.
        ''' </summary>
        ''' <param name="limit">Maximo de velas a cargar (por defecto 500)</param>
        Public Async Function GetCandlesAsync(platform As String,
                                               symbol As String,
                                               timeframe As String,
                                               Optional limit As Integer = 500) As Task(Of List(Of CandleModel))
            Dim resultado As New List(Of CandleModel)()
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    ' Traemos las N mas recientes y luego invertimos para tener oldest→newest
                    Dim sql As String =
                        "SELECT epoch, open_price, high_price, low_price, close_price " &
                        "FROM ( " &
                        "  SELECT epoch, open_price, high_price, low_price, close_price " &
                        "  FROM public.candles " &
                        "  WHERE platform=@platform AND symbol=@symbol AND timeframe=@timeframe " &
                        "  ORDER BY epoch DESC " &
                        "  LIMIT @lim " &
                        ") sub " &
                        "ORDER BY epoch ASC;"

                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("platform", platform)
                        cmd.Parameters.AddWithValue("symbol", symbol)
                        cmd.Parameters.AddWithValue("timeframe", timeframe)
                        cmd.Parameters.AddWithValue("lim", limit)

                        Using reader = Await cmd.ExecuteReaderAsync()
                            Dim epochBase As New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                            Do While Await reader.ReadAsync()
                                Dim m As New CandleModel()
                                m.Epoch = CLng(reader("epoch"))
                                m.OpenTime = epochBase.AddSeconds(m.Epoch).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                                m.OpenPrice = CDec(reader("open_price"))
                                m.High = CDec(reader("high_price"))
                                m.Low = CDec(reader("low_price"))
                                m.ClosePrice = CDec(reader("close_price"))
                                m.Status = "[BD]"
                                resultado.Add(m)
                            Loop
                        End Using
                    End Using
                End Using
                Console.WriteLine(String.Format("[CandleRepository.GetCandlesAsync] Cargadas {0} velas de {1} {2} {3}", resultado.Count, platform, symbol, timeframe))
            Catch ex As Exception
                Console.WriteLine("[CandleRepository.GetCandlesAsync] Error: " & ex.Message)
            End Try
            Return resultado
        End Function

        ' ---- Helper para asignar parametros ----

        Private Shared Sub AddCandleParams(cmd As NpgsqlCommand,
                                            platform As String,
                                            symbol As String,
                                            timeframe As String,
                                            candle As CandleModel,
                                            isClosed As Boolean)
            Dim epochBase As New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            Dim openTimeUtc As DateTime = epochBase.AddSeconds(candle.Epoch)

            cmd.Parameters.AddWithValue("platform", platform)
            cmd.Parameters.AddWithValue("symbol", symbol)
            cmd.Parameters.AddWithValue("timeframe", timeframe)
            cmd.Parameters.AddWithValue("epoch", candle.Epoch)
            cmd.Parameters.AddWithValue("open_time", openTimeUtc)
            cmd.Parameters.AddWithValue("open_price", candle.OpenPrice)
            cmd.Parameters.AddWithValue("high_price", candle.High)
            cmd.Parameters.AddWithValue("low_price", candle.Low)
            cmd.Parameters.AddWithValue("close_price", candle.ClosePrice)
            cmd.Parameters.AddWithValue("is_closed", isClosed)
        End Sub
    End Class
End Namespace
