Imports System.Collections.Generic
Imports System.Threading.Tasks
Imports Npgsql
Imports NpgsqlTypes

Namespace WpfDerivClientVB.Backtesting

    ''' <summary>
    ''' Repositorio PostgreSQL para sesiones y operaciones de backtesting.
    ''' Patron identico a ClientRepository: constructor sin argumentos,
    ''' DDL inline en EnsureTablesExistAsync.
    ''' </summary>
    Public Class BacktestRepository

        Private ReadOnly _connectionString As String

        Public Sub New()
            Dim config = ConfigManager.LoadDbConfig()
            _connectionString = config.GetConnectionString()
        End Sub

        ' ================================================================
        '  DDL
        ' ================================================================

        ''' <summary>
        ''' Crea las tablas backtest_sesiones y backtest_operaciones si no existen.
        ''' Llamar antes de guardar resultados.
        ''' </summary>
        Public Async Function EnsureTablesExistAsync() As Task
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()

                    Dim sqlSesiones =
                        "CREATE TABLE IF NOT EXISTS public.backtest_sesiones (" &
                        "    id                SERIAL PRIMARY KEY," &
                        "    estrategia        VARCHAR(50)     NOT NULL," &
                        "    symbol            VARCHAR(50)     NOT NULL," &
                        "    timeframe         VARCHAR(10)     NOT NULL," &
                        "    desde_epoch       BIGINT          NOT NULL," &
                        "    hasta_epoch       BIGINT          NOT NULL," &
                        "    duracion_velas    INTEGER         NOT NULL," &
                        "    payout_pct        DECIMAL(6,2)    NOT NULL," &
                        "    min_confianza     INTEGER         NOT NULL," &
                        "    total_senales     INTEGER         NOT NULL DEFAULT 0," &
                        "    total_operaciones INTEGER         NOT NULL DEFAULT 0," &
                        "    win_rate          DECIMAL(6,4)    NOT NULL DEFAULT 0," &
                        "    profit_loss_total DECIMAL(14,4)   NOT NULL DEFAULT 0," &
                        "    max_drawdown      DECIMAL(14,4)   NOT NULL DEFAULT 0," &
                        "    profit_factor     DECIMAL(10,4)   NOT NULL DEFAULT 0," &
                        "    created_at        TIMESTAMP       NOT NULL DEFAULT CURRENT_TIMESTAMP," &
                        "    notas             TEXT" &
                        ");"

                    Dim sqlOperaciones =
                        "CREATE TABLE IF NOT EXISTS public.backtest_operaciones (" &
                        "    id               SERIAL PRIMARY KEY," &
                        "    sesion_id        INTEGER         NOT NULL REFERENCES public.backtest_sesiones(id) ON DELETE CASCADE," &
                        "    cuenta_nombre    VARCHAR(100)    NOT NULL," &
                        "    tipo             VARCHAR(4)      NOT NULL," &
                        "    epoch_entrada    BIGINT          NOT NULL," &
                        "    precio_entrada   DECIMAL(18,6)   NOT NULL," &
                        "    epoch_salida     BIGINT          NOT NULL," &
                        "    precio_salida    DECIMAL(18,6)   NOT NULL," &
                        "    monto            DECIMAL(14,4)   NOT NULL," &
                        "    payout_pct       DECIMAL(6,2)    NOT NULL," &
                        "    estado           VARCHAR(10)     NOT NULL," &
                        "    profit_loss      DECIMAL(14,4)   NOT NULL DEFAULT 0," &
                        "    balance_post     DECIMAL(14,4)   NOT NULL," &
                        "    confianza        INTEGER         NOT NULL," &
                        "    motivo_senal     VARCHAR(500)," &
                        "    datos_indicadores TEXT," &
                        "    contexto_velas  TEXT," &
                        "    created_at       TIMESTAMP       NOT NULL DEFAULT CURRENT_TIMESTAMP" &
                        ");"

                    Dim sqlIdx1 = "CREATE INDEX IF NOT EXISTS idx_bt_ops_sesion ON public.backtest_operaciones(sesion_id);"
                    Dim sqlIdx2 = "CREATE INDEX IF NOT EXISTS idx_bt_ops_estado ON public.backtest_operaciones(sesion_id, estado);"
                    Dim sqlIdx3 = "CREATE INDEX IF NOT EXISTS idx_bt_ops_cuenta ON public.backtest_operaciones(sesion_id, cuenta_nombre);"

                    For Each ddl In {sqlSesiones, sqlOperaciones, sqlIdx1, sqlIdx2, sqlIdx3}
                        Using cmd As New NpgsqlCommand(ddl, conn)
                            Await cmd.ExecuteNonQueryAsync()
                        End Using
                    Next
                End Using
            Catch ex As Exception
                Throw New Exception("Error al inicializar tablas de backtesting: " & ex.Message)
            End Try
        End Function

        ' ================================================================
        '  ESCRITURA
        ' ================================================================

        ''' <summary>
        ''' Guarda la sesion y todas sus operaciones en una sola transaccion.
        ''' Devuelve el ID de la sesion generado.
        ''' </summary>
        Public Async Function GuardarSesionConOperacionesAsync(resultado As BacktestResultado) As Task(Of Integer)
            Dim sesionId As Integer = 0
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Using tx = conn.BeginTransaction()
                        Try
                            ' -- Insertar sesion -------------------------------------------
                            Dim s = resultado.Sesion
                            Dim sqlSesion =
                                "INSERT INTO public.backtest_sesiones " &
                                "  (estrategia, symbol, timeframe, desde_epoch, hasta_epoch, " &
                                "   duracion_velas, payout_pct, min_confianza, total_senales, " &
                                "   total_operaciones, win_rate, profit_loss_total, max_drawdown, " &
                                "   profit_factor, notas) " &
                                "VALUES (@est, @sym, @tf, @de, @he, @dv, @pay, @mc, @ts, @top, " &
                                "        @wr, @plt, @md, @pf, @notas) " &
                                "RETURNING id;"

                            Using cmd As New NpgsqlCommand(sqlSesion, conn, tx)
                                cmd.Parameters.AddWithValue("est",   s.Estrategia)
                                cmd.Parameters.AddWithValue("sym",   s.Symbol)
                                cmd.Parameters.AddWithValue("tf",    s.Timeframe)
                                cmd.Parameters.AddWithValue("de",    s.DesdeEpoch)
                                cmd.Parameters.AddWithValue("he",    s.HastaEpoch)
                                cmd.Parameters.AddWithValue("dv",    s.DuracionVelas)
                                cmd.Parameters.AddWithValue("pay",   s.PayoutPct)
                                cmd.Parameters.AddWithValue("mc",    s.MinConfianza)
                                cmd.Parameters.AddWithValue("ts",    s.TotalSenales)
                                cmd.Parameters.AddWithValue("top",   s.TotalOperaciones)
                                cmd.Parameters.AddWithValue("wr",    s.WinRate)
                                cmd.Parameters.AddWithValue("plt",   s.ProfitLossTotal)
                                cmd.Parameters.AddWithValue("md",    s.MaxDrawdown)
                                cmd.Parameters.AddWithValue("pf",    s.ProfitFactor)
                                cmd.Parameters.AddWithValue("notas", If(s.Notas, CType(DBNull.Value, Object)))
                                sesionId = CInt(Await cmd.ExecuteScalarAsync())
                            End Using

                            ' -- Insertar operaciones via COPY binario (PostgreSQL) -------
                            ' ~50x mas rapido que INSERT por fila para volumenes grandes (cientos de miles).
                            ' Importante: el COPY requiere su propio commit; cerramos la transaccion antes
                            ' para que el writer use la conexion sin choques de tx.
                            tx.Commit()

                            Dim copySql =
                                "COPY public.backtest_operaciones " &
                                "(sesion_id, cuenta_nombre, tipo, epoch_entrada, precio_entrada, " &
                                " epoch_salida, precio_salida, monto, payout_pct, estado, " &
                                " profit_loss, balance_post, confianza, motivo_senal, " &
                                " datos_indicadores, contexto_velas) FROM STDIN (FORMAT BINARY)"

                            Using writer = conn.BeginBinaryImport(copySql)
                                For Each op In resultado.Operaciones
                                    writer.StartRow()
                                    writer.Write(sesionId, NpgsqlDbType.Integer)
                                    writer.Write(If(op.CuentaNombre, ""), NpgsqlDbType.Varchar)
                                    writer.Write(If(op.Tipo, ""), NpgsqlDbType.Varchar)
                                    writer.Write(op.EpochEntrada, NpgsqlDbType.Bigint)
                                    writer.Write(op.PrecioEntrada, NpgsqlDbType.Numeric)
                                    writer.Write(op.EpochSalida, NpgsqlDbType.Bigint)
                                    writer.Write(op.PrecioSalida, NpgsqlDbType.Numeric)
                                    writer.Write(op.Monto, NpgsqlDbType.Numeric)
                                    writer.Write(op.PayoutPct, NpgsqlDbType.Numeric)
                                    writer.Write(If(op.Estado, ""), NpgsqlDbType.Varchar)
                                    writer.Write(op.ProfitLoss, NpgsqlDbType.Numeric)
                                    writer.Write(op.BalancePost, NpgsqlDbType.Numeric)
                                    writer.Write(op.Confianza, NpgsqlDbType.Integer)
                                    If String.IsNullOrEmpty(op.MotivoSenal) Then
                                        writer.WriteNull()
                                    Else
                                        writer.Write(op.MotivoSenal, NpgsqlDbType.Varchar)
                                    End If
                                    If String.IsNullOrEmpty(op.DatosIndicadores) Then
                                        writer.WriteNull()
                                    Else
                                        writer.Write(op.DatosIndicadores, NpgsqlDbType.Text)
                                    End If
                                    If String.IsNullOrEmpty(op.ContextoVelas) Then
                                        writer.WriteNull()
                                    Else
                                        writer.Write(op.ContextoVelas, NpgsqlDbType.Text)
                                    End If
                                Next
                                writer.Complete()
                            End Using
                        Catch ex As Exception
                            Try
                                tx.Rollback()
                            Catch
                                ' tx puede ya estar commiteada (caso COPY) — ignorar
                            End Try
                            Throw
                        End Try
                    End Using
                End Using
            Catch ex As Exception
                Console.WriteLine("[BacktestRepository.GuardarSesionConOperacionesAsync] Error: " & ex.Message)
                Throw
            End Try
            Return sesionId
        End Function

        ' ================================================================
        '  LECTURA
        ' ================================================================

        ''' <summary>
        ''' Devuelve las ultimas N sesiones ordenadas por fecha descendente.
        ''' </summary>
        Public Async Function GetSesionesAsync(Optional limite As Integer = 20) As Task(Of List(Of BacktestSesion))
            Dim lista As New List(Of BacktestSesion)()
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "SELECT id, estrategia, symbol, timeframe, desde_epoch, hasta_epoch, " &
                        "       duracion_velas, payout_pct, min_confianza, total_senales, " &
                        "       total_operaciones, win_rate, profit_loss_total, max_drawdown, " &
                        "       profit_factor, created_at, notas " &
                        "FROM public.backtest_sesiones " &
                        "ORDER BY created_at DESC " &
                        "LIMIT @lim;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("lim", limite)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            Do While Await reader.ReadAsync()
                                lista.Add(MapSesion(reader))
                            Loop
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                Console.WriteLine("[BacktestRepository.GetSesionesAsync] Error: " & ex.Message)
            End Try
            Return lista
        End Function

        ''' <summary>
        ''' Todas las sesiones de una estrategia en orden cronologico (para analisis de evolucion).
        ''' </summary>
        Public Async Function GetSesionesPorEstrategiaAsync(estrategia As String, Optional limite As Integer = 50) As Task(Of List(Of BacktestSesion))
            Dim lista As New List(Of BacktestSesion)()
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "SELECT id, estrategia, symbol, timeframe, desde_epoch, hasta_epoch, " &
                        "       duracion_velas, payout_pct, min_confianza, total_senales, " &
                        "       total_operaciones, win_rate, profit_loss_total, max_drawdown, " &
                        "       profit_factor, created_at, notas " &
                        "FROM public.backtest_sesiones " &
                        "WHERE estrategia = @est " &
                        "ORDER BY created_at ASC " &
                        "LIMIT @lim;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("est", estrategia)
                        cmd.Parameters.AddWithValue("lim", limite)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            Do While Await reader.ReadAsync()
                                lista.Add(MapSesion(reader))
                            Loop
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                Console.WriteLine("[BacktestRepository.GetSesionesPorEstrategiaAsync] Error: " & ex.Message)
            End Try
            Return lista
        End Function

        ''' <summary>
        ''' Una sesion especifica por ID.
        ''' </summary>
        Public Async Function GetSesionPorIdAsync(sesionId As Integer) As Task(Of BacktestSesion)
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "SELECT id, estrategia, symbol, timeframe, desde_epoch, hasta_epoch, " &
                        "       duracion_velas, payout_pct, min_confianza, total_senales, " &
                        "       total_operaciones, win_rate, profit_loss_total, max_drawdown, " &
                        "       profit_factor, created_at, notas " &
                        "FROM public.backtest_sesiones WHERE id = @id;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("id", sesionId)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            If Await reader.ReadAsync() Then Return MapSesion(reader)
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                Console.WriteLine("[BacktestRepository.GetSesionPorIdAsync] Error: " & ex.Message)
            End Try
            Return Nothing
        End Function

        ''' <summary>
        ''' Todas las operaciones de una sesion, ordenadas por epoch de entrada.
        ''' </summary>
        Public Async Function GetOperacionesPorSesionAsync(sesionId As Integer) As Task(Of List(Of BacktestOperacion))
            Return Await GetOperacionesInternoAsync(sesionId, Nothing)
        End Function

        ''' <summary>
        ''' Devuelve las ultimas N operaciones de una sesion (ORDER BY epoch DESC LIMIT n).
        ''' Util para mostrar en UI sin cargar cientos de miles de filas.
        ''' </summary>
        Public Async Function GetUltimasOperacionesAsync(sesionId As Integer, limite As Integer) As Task(Of List(Of BacktestOperacion))
            Dim lista As New List(Of BacktestOperacion)()
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "SELECT id, sesion_id, cuenta_nombre, tipo, epoch_entrada, precio_entrada, " &
                        "       epoch_salida, precio_salida, monto, payout_pct, estado, profit_loss, " &
                        "       balance_post, confianza, motivo_senal, datos_indicadores, contexto_velas " &
                        "FROM public.backtest_operaciones " &
                        "WHERE sesion_id = @sid " &
                        "ORDER BY epoch_entrada DESC LIMIT @lim;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("sid", sesionId)
                        cmd.Parameters.AddWithValue("lim", limite)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            While Await reader.ReadAsync()
                                lista.Add(MapOperacion(reader))
                            End While
                        End Using
                    End Using
                End Using
                ' Invertir para mostrar orden cronologico
                lista.Reverse()
            Catch ex As Exception
                Console.WriteLine("[BacktestRepository.GetUltimasOperacionesAsync] Error: " & ex.Message)
            End Try
            Return lista
        End Function

        ''' <summary>
        ''' Cuenta total de operaciones de una sesion (sin traer los datos).
        ''' </summary>
        Public Async Function ContarOperacionesAsync(sesionId As Integer) As Task(Of Long)
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Using cmd As New NpgsqlCommand(
                        "SELECT COUNT(*) FROM public.backtest_operaciones WHERE sesion_id = @sid;", conn)
                        cmd.Parameters.AddWithValue("sid", sesionId)
                        Return CLng(Await cmd.ExecuteScalarAsync())
                    End Using
                End Using
            Catch
                Return 0
            End Try
        End Function

        ''' <summary>
        ''' Solo las operaciones perdidas de una sesion, con contexto de velas para analisis.
        ''' </summary>
        Public Async Function GetPerdidasConContextoAsync(sesionId As Integer) As Task(Of List(Of BacktestOperacion))
            Return Await GetOperacionesInternoAsync(sesionId, "Perdido")
        End Function

        ' ================================================================
        '  HELPERS PRIVADOS
        ' ================================================================

        Private Async Function GetOperacionesInternoAsync(sesionId As Integer, Optional soloEstado As String = Nothing) As Task(Of List(Of BacktestOperacion))
            Dim lista As New List(Of BacktestOperacion)()
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim filtro = If(soloEstado IsNot Nothing, " AND estado = @estado", "")
                    Dim sql =
                        "SELECT id, sesion_id, cuenta_nombre, tipo, epoch_entrada, precio_entrada, " &
                        "       epoch_salida, precio_salida, monto, payout_pct, estado, profit_loss, " &
                        "       balance_post, confianza, motivo_senal, datos_indicadores, contexto_velas " &
                        "FROM public.backtest_operaciones " &
                        "WHERE sesion_id = @sid" & filtro &
                        " ORDER BY epoch_entrada ASC;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("sid", sesionId)
                        If soloEstado IsNot Nothing Then cmd.Parameters.AddWithValue("estado", soloEstado)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            Do While Await reader.ReadAsync()
                                lista.Add(MapOperacion(reader))
                            Loop
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                Console.WriteLine("[BacktestRepository.GetOperacionesInternoAsync] Error: " & ex.Message)
            End Try
            Return lista
        End Function

        ''' <summary>
        ''' Elimina una sesion y TODAS sus operaciones asociadas, garantizando
        ''' que no queden registros huerfanos. Usa una transaccion atomica:
        ''' si algo falla, ni la sesion ni sus operaciones se borran.
        ''' Devuelve (operacionesBorradas, sesionBorrada) — sesionBorrada=true si
        ''' efectivamente existia esa fila.
        ''' Triple proteccion contra huerfanos:
        '''  1) DDL: backtest_operaciones.sesion_id REFERENCES ... ON DELETE CASCADE
        '''  2) DELETE explicito de operaciones primero
        '''  3) Verificacion post-delete: count(*) WHERE sesion_id = id == 0
        ''' </summary>
        Public Async Function EliminarSesionAsync(sesionId As Integer) As Task(Of (OpsBorradas As Integer, SesionBorrada As Boolean))
            Dim opsBorradas As Integer = 0
            Dim sesionBorrada As Boolean = False
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Using tx = conn.BeginTransaction()
                        Try
                            ' 1) Borrar operaciones asociadas
                            Using cmd As New NpgsqlCommand(
                                "DELETE FROM public.backtest_operaciones WHERE sesion_id = @sid;",
                                conn, tx)
                                cmd.Parameters.AddWithValue("sid", sesionId)
                                opsBorradas = Await cmd.ExecuteNonQueryAsync()
                            End Using

                            ' 2) Borrar la sesion
                            Using cmd As New NpgsqlCommand(
                                "DELETE FROM public.backtest_sesiones WHERE id = @sid;",
                                conn, tx)
                                cmd.Parameters.AddWithValue("sid", sesionId)
                                Dim n = Await cmd.ExecuteNonQueryAsync()
                                sesionBorrada = (n > 0)
                            End Using

                            ' 3) Verificacion: confirmar que NO quedan ops huerfanas
                            Using cmd As New NpgsqlCommand(
                                "SELECT COUNT(*) FROM public.backtest_operaciones WHERE sesion_id = @sid;",
                                conn, tx)
                                cmd.Parameters.AddWithValue("sid", sesionId)
                                Dim huerfanos = CLng(Await cmd.ExecuteScalarAsync())
                                If huerfanos > 0 Then
                                    Throw New Exception(
                                        String.Format("Quedaron {0} operaciones huerfanas para sesion #{1} — abortando.",
                                                       huerfanos, sesionId))
                                End If
                            End Using

                            tx.Commit()
                        Catch
                            Try
                                tx.Rollback()
                            Catch
                            End Try
                            Throw
                        End Try
                    End Using
                End Using
            Catch ex As Exception
                Console.WriteLine("[BacktestRepository.EliminarSesionAsync] Error: " & ex.Message)
                Throw
            End Try
            Return (opsBorradas, sesionBorrada)
        End Function

        Private Shared Function MapSesion(reader As Npgsql.NpgsqlDataReader) As BacktestSesion
            Return New BacktestSesion With {
                .Id               = CInt(reader("id")),
                .Estrategia       = reader("estrategia").ToString(),
                .Symbol           = reader("symbol").ToString(),
                .Timeframe        = reader("timeframe").ToString(),
                .DesdeEpoch       = CLng(reader("desde_epoch")),
                .HastaEpoch       = CLng(reader("hasta_epoch")),
                .DuracionVelas    = CInt(reader("duracion_velas")),
                .PayoutPct        = CDec(reader("payout_pct")),
                .MinConfianza     = CInt(reader("min_confianza")),
                .TotalSenales     = CInt(reader("total_senales")),
                .TotalOperaciones = CInt(reader("total_operaciones")),
                .WinRate          = CDec(reader("win_rate")),
                .ProfitLossTotal  = CDec(reader("profit_loss_total")),
                .MaxDrawdown      = CDec(reader("max_drawdown")),
                .ProfitFactor     = CDec(reader("profit_factor")),
                .CreatedAt        = CDate(reader("created_at")),
                .Notas            = If(IsDBNull(reader("notas")), "", reader("notas").ToString())
            }
        End Function

        Private Shared Function MapOperacion(reader As Npgsql.NpgsqlDataReader) As BacktestOperacion
            Return New BacktestOperacion With {
                .SesionId         = CInt(reader("sesion_id")),
                .CuentaNombre     = reader("cuenta_nombre").ToString(),
                .Tipo             = reader("tipo").ToString(),
                .EpochEntrada     = CLng(reader("epoch_entrada")),
                .PrecioEntrada    = CDec(reader("precio_entrada")),
                .EpochSalida      = CLng(reader("epoch_salida")),
                .PrecioSalida     = CDec(reader("precio_salida")),
                .Monto            = CDec(reader("monto")),
                .PayoutPct        = CDec(reader("payout_pct")),
                .Estado           = reader("estado").ToString(),
                .ProfitLoss       = CDec(reader("profit_loss")),
                .BalancePost      = CDec(reader("balance_post")),
                .Confianza        = CInt(reader("confianza")),
                .MotivoSenal      = If(IsDBNull(reader("motivo_senal")),      "", reader("motivo_senal").ToString()),
                .DatosIndicadores = If(IsDBNull(reader("datos_indicadores")), "", reader("datos_indicadores").ToString()),
                .ContextoVelas    = If(IsDBNull(reader("contexto_velas")),    "", reader("contexto_velas").ToString())
            }
        End Function

    End Class

End Namespace
