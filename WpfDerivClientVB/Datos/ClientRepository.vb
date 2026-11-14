Imports System.Collections.Generic
Imports System.Threading.Tasks
Imports Npgsql

Namespace WpfDerivClientVB
    ''' <summary>
    ''' Repositorio para la gestión de clientes y sus credenciales en PostgreSQL.
    ''' </summary>
    Public Class ClientRepository
        Private ReadOnly _connectionString As String

        Public Sub New()
            Dim config = ConfigManager.LoadDbConfig()
            _connectionString = config.GetConnectionString()
        End Sub

        ''' <summary>
        ''' Crea las tablas necesarias si no existen. Se llama al iniciar la vista.
        ''' </summary>
        Public Async Function EnsureTablesExistAsync() As Task
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()

                    Dim sqlClients = "
                        CREATE TABLE IF NOT EXISTS public.clients (
                            id SERIAL PRIMARY KEY,
                            name VARCHAR(100) NOT NULL,
                            email VARCHAR(150),
                            active BOOLEAN DEFAULT TRUE,
                            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                        );"

                    Dim sqlCreds = "
                        CREATE TABLE IF NOT EXISTS public.client_deriv_credentials (
                            id SERIAL PRIMARY KEY,
                            client_id INTEGER NOT NULL REFERENCES public.clients(id) ON DELETE CASCADE,
                            account_id_real VARCHAR(50),
                            app_id_real VARCHAR(100),
                            api_token_real TEXT,
                            account_id_virtual VARCHAR(50),
                            app_id_virtual VARCHAR(100),
                            api_token_virtual TEXT,
                            UNIQUE(client_id)
                        );"

                    Dim sqlMigrateReal = "ALTER TABLE public.client_deriv_credentials ADD COLUMN IF NOT EXISTS account_id_real VARCHAR(50);"
                    Dim sqlMigrateVir = "ALTER TABLE public.client_deriv_credentials ADD COLUMN IF NOT EXISTS account_id_virtual VARCHAR(50);"
                    Dim sqlMigrateAppIdReal = "ALTER TABLE public.client_deriv_credentials ALTER COLUMN app_id_real TYPE VARCHAR(100);"
                    ' TEXT: un token cifrado con DPAPI ocupa ~300 caracteres (no cabe en VARCHAR(200))
                    Dim sqlMigrateTokenReal = "ALTER TABLE public.client_deriv_credentials ALTER COLUMN api_token_real TYPE TEXT;"
                    Dim sqlMigrateAppIdVir = "ALTER TABLE public.client_deriv_credentials ALTER COLUMN app_id_virtual TYPE VARCHAR(100);"
                    Dim sqlMigrateTokenVir = "ALTER TABLE public.client_deriv_credentials ALTER COLUMN api_token_virtual TYPE TEXT;"
                    Dim sqlMigrateTelReal = "ALTER TABLE public.client_deriv_credentials ADD COLUMN IF NOT EXISTS telefono_real VARCHAR(50);"
                    Dim sqlMigrateTelVir  = "ALTER TABLE public.client_deriv_credentials ADD COLUMN IF NOT EXISTS telefono_virtual VARCHAR(50);"

                    Using cmd As New NpgsqlCommand(sqlClients, conn)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                    Using cmd As New NpgsqlCommand(sqlCreds, conn)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                    For Each migSql In {sqlMigrateReal, sqlMigrateVir, sqlMigrateAppIdReal, sqlMigrateTokenReal,
                                        sqlMigrateAppIdVir, sqlMigrateTokenVir, sqlMigrateTelReal, sqlMigrateTelVir}
                        Try
                            Using cmd As New NpgsqlCommand(migSql, conn)
                                Await cmd.ExecuteNonQueryAsync()
                            End Using
                        Catch
                        End Try
                    Next

                    ' Cifrar tokens que sigan en texto plano (idempotente)
                    Await MigrarTokensACifradoAsync(conn)
                End Using
            Catch ex As Exception
                Throw New Exception("Error al inicializar tablas: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' Cifra con DPAPI los tokens que aun esten en texto plano.
        ''' Idempotente: los ya cifrados (prefijo "dpapi:") no se tocan.
        ''' </summary>
        Private Async Function MigrarTokensACifradoAsync(conn As NpgsqlConnection) As Task
            Dim pendientes As New List(Of Tuple(Of Integer, String, String))()
            Dim sqlSel = "SELECT id, COALESCE(api_token_real,''), COALESCE(api_token_virtual,'') " &
                         "FROM public.client_deriv_credentials " &
                         "WHERE (COALESCE(api_token_real,'') <> '' AND api_token_real NOT LIKE 'dpapi:%') " &
                         "   OR (COALESCE(api_token_virtual,'') <> '' AND api_token_virtual NOT LIKE 'dpapi:%')"
            Using cmd As New NpgsqlCommand(sqlSel, conn)
                Using reader = Await cmd.ExecuteReaderAsync()
                    While Await reader.ReadAsync()
                        pendientes.Add(Tuple.Create(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)))
                    End While
                End Using
            End Using
            If pendientes.Count = 0 Then Return

            For Each p In pendientes
                Using cmd As New NpgsqlCommand(
                    "UPDATE public.client_deriv_credentials SET api_token_real = @r, api_token_virtual = @v WHERE id = @id", conn)
                    cmd.Parameters.AddWithValue("r", SecretProtector.Protect(p.Item2))
                    cmd.Parameters.AddWithValue("v", SecretProtector.Protect(p.Item3))
                    cmd.Parameters.AddWithValue("id", p.Item1)
                    Await cmd.ExecuteNonQueryAsync()
                End Using
            Next
            AppLogger.Info("[ClientRepository] Tokens cifrados con DPAPI en " & pendientes.Count & " registro(s).")
        End Function

        ''' <summary>
        ''' Obtiene todos los clientes del sistema.
        ''' </summary>
        Public Async Function GetAllAsync() As Task(Of List(Of ClientModel))
            Dim clients As New List(Of ClientModel)()
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql As String = "SELECT * FROM public.clients ORDER BY name ASC"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            While Await reader.ReadAsync()
                                clients.Add(New ClientModel With {
                                    .Id = CInt(reader("id")),
                                    .Name = reader("name").ToString(),
                                    .Email = reader("email").ToString(),
                                    .Active = CBool(reader("active")),
                                    .CreatedAt = CDate(reader("created_at"))
                                })
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                Throw New Exception("Error al cargar clientes: " & ex.Message)
            End Try
            Return clients
        End Function

        ''' <summary>
        ''' Obtiene las credenciales Deriv de un cliente por su ID.
        ''' </summary>
        Public Async Function GetDerivCredentialsAsync(clientId As Integer) As Task(Of ClientDerivCredentials)
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql As String = "SELECT * FROM public.client_deriv_credentials WHERE client_id = @clientId"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("clientId", clientId)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            If Await reader.ReadAsync() Then
                                ' Helper local: lee columna como string, devuelve "" si DBNull o no existe
                                Dim safe = Function(col As String) As String
                                               Try
                                                   Dim v = reader(col)
                                                   If v Is Nothing OrElse IsDBNull(v) Then Return ""
                                                   Return v.ToString()
                                               Catch
                                                   Return ""
                                               End Try
                                           End Function
                                Return New ClientDerivCredentials With {
                                    .AccountIdReal = safe("account_id_real"),
                                    .AppIdReal = safe("app_id_real"),
                                    .ApiTokenReal = SecretProtector.Unprotect(safe("api_token_real")),
                                    .TelefonoReal = safe("telefono_real"),
                                    .AccountIdVirtual = safe("account_id_virtual"),
                                    .AppIdVirtual = safe("app_id_virtual"),
                                    .ApiTokenVirtual = SecretProtector.Unprotect(safe("api_token_virtual")),
                                    .TelefonoVirtual = safe("telefono_virtual")
                                }
                            End If
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                Throw New Exception("Error al obtener credenciales Deriv: " & ex.Message)
            End Try
            Return Nothing
        End Function

        ''' <summary>
        ''' Guarda o actualiza un cliente y sus credenciales.
        ''' </summary>
        Public Async Function SaveClientAsync(client As ClientModel) As Task
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Using trans = conn.BeginTransaction()
                        Try
                            Dim clientSql As String
                            If client.Id = 0 Then
                                ' Insertar nuevo cliente
                                clientSql = "INSERT INTO public.clients (name, email) VALUES (@name, @email) RETURNING id"
                            Else
                                ' Actualizar cliente existente
                                clientSql = "UPDATE public.clients SET name = @name, email = @email WHERE id = @id"
                            End If

                            Dim clientId As Integer = client.Id
                            Using cmd = New NpgsqlCommand(clientSql, conn, trans)
                                cmd.Parameters.AddWithValue("name", client.Name)
                                cmd.Parameters.AddWithValue("email", client.Email)
                                If client.Id <> 0 Then cmd.Parameters.AddWithValue("id", client.Id)
                                
                                If client.Id = 0 Then
                                    clientId = CInt(Await cmd.ExecuteScalarAsync())
                                    client.Id = clientId
                                Else
                                    Await cmd.ExecuteNonQueryAsync()
                                End If
                            End Using

                            ' Actualizar credenciales de Deriv vinculadas
                            If client.DerivCredentials IsNot Nothing Then
                                Dim credsSql = "
                                    DELETE FROM public.client_deriv_credentials WHERE client_id = @cid;
                                    INSERT INTO public.client_deriv_credentials (client_id, account_id_real, app_id_real, api_token_real, telefono_real, account_id_virtual, app_id_virtual, api_token_virtual, telefono_virtual)
                                    VALUES (@cid, @acir, @air, @atr, @telr, @aciv, @aiv, @atv, @telv);"

                                Using cmdCreds = New NpgsqlCommand(credsSql, conn, trans)
                                    cmdCreds.Parameters.AddWithValue("cid", clientId)
                                    cmdCreds.Parameters.AddWithValue("acir", If(client.DerivCredentials.AccountIdReal, ""))
                                    cmdCreds.Parameters.AddWithValue("air", If(client.DerivCredentials.AppIdReal, ""))
                                    cmdCreds.Parameters.AddWithValue("atr", SecretProtector.Protect(If(client.DerivCredentials.ApiTokenReal, "")))
                                    cmdCreds.Parameters.AddWithValue("telr", If(client.DerivCredentials.TelefonoReal, ""))
                                    cmdCreds.Parameters.AddWithValue("aciv", If(client.DerivCredentials.AccountIdVirtual, ""))
                                    cmdCreds.Parameters.AddWithValue("aiv", If(client.DerivCredentials.AppIdVirtual, ""))
                                    cmdCreds.Parameters.AddWithValue("atv", SecretProtector.Protect(If(client.DerivCredentials.ApiTokenVirtual, "")))
                                    cmdCreds.Parameters.AddWithValue("telv", If(client.DerivCredentials.TelefonoVirtual, ""))
                                    Await cmdCreds.ExecuteNonQueryAsync()
                                End Using
                            End If

                            trans.Commit()
                        Catch
                            trans.Rollback()
                            Throw
                        End Try
                    End Using
                End Using
            Catch ex As Exception
                Throw New Exception("Error al guardar cliente: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' Crea la tabla de operaciones si no existe.
        ''' </summary>
        Public Async Function EnsureOperacionesTableAsync() As Task
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql = "
                        CREATE TABLE IF NOT EXISTS public.trading_operations (
                            id SERIAL PRIMARY KEY,
                            client_id INTEGER REFERENCES public.clients(id),
                            contract_id VARCHAR(100),
                            tipo VARCHAR(10),
                            simbolo VARCHAR(50),
                            monto DECIMAL(12,4),
                            duracion VARCHAR(50),
                            estado VARCHAR(20),
                            profit_loss DECIMAL(12,4) DEFAULT 0,
                            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                        );"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                End Using
            Catch ex As Exception
                Throw New Exception("Error al crear tabla operaciones: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' Obtiene las últimas operaciones de un cliente desde la BD.
        ''' </summary>
        Public Async Function ObtenerOperacionesAsync(clientId As Integer, Optional limite As Integer = 50) As Task(Of List(Of OperacionModel))
            Dim lista As New List(Of OperacionModel)()
            Try
                Await EnsureOperacionesTableAsync()
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql = "SELECT id, client_id, contract_id, tipo, simbolo, monto, duracion, estado, profit_loss, created_at
                               FROM public.trading_operations
                               WHERE client_id = @cid
                               ORDER BY created_at DESC
                               LIMIT @lim"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("cid", clientId)
                        cmd.Parameters.AddWithValue("lim", limite)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            Do While Await reader.ReadAsync()
                                Dim op As New OperacionModel()
                                op.Id = reader.GetInt32(0)
                                op.ClientId = If(reader.IsDBNull(1), 0, reader.GetInt32(1))
                                op.ContractId = If(reader.IsDBNull(2), "", reader.GetString(2))
                                op.Tipo = If(reader.IsDBNull(3), "", reader.GetString(3))
                                op.Simbolo = If(reader.IsDBNull(4), "", reader.GetString(4))
                                op.Monto = If(reader.IsDBNull(5), 0D, reader.GetDecimal(5))
                                op.Duracion = If(reader.IsDBNull(6), "", reader.GetString(6))
                                op.Estado = If(reader.IsDBNull(7), "", reader.GetString(7))
                                op.ProfitLoss = If(reader.IsDBNull(8), 0D, reader.GetDecimal(8))
                                op.CreatedAt = If(reader.IsDBNull(9), DateTime.UtcNow, reader.GetDateTime(9))
                                op.Hora = op.CreatedAt.ToLocalTime().ToString("HH:mm:ss")
                                lista.Add(op)
                            Loop
                        End Using
                    End Using
                End Using
            Catch
            End Try
            Return lista
        End Function

        ''' <summary>
        ''' Guarda una operación de trading en la BD.
        ''' </summary>
        Public Async Function GuardarOperacionAsync(op As OperacionModel) As Task
            Try
                Await EnsureOperacionesTableAsync()
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql = "INSERT INTO public.trading_operations (client_id, contract_id, tipo, simbolo, monto, duracion, estado, profit_loss)
                               VALUES (@cid, @coid, @tipo, @sim, @monto, @dur, @est, @pl)"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("cid", If(op.ClientId > 0, CObj(op.ClientId), DBNull.Value))
                        cmd.Parameters.AddWithValue("coid", If(op.ContractId, ""))
                        cmd.Parameters.AddWithValue("tipo", If(op.Tipo, ""))
                        cmd.Parameters.AddWithValue("sim", If(op.Simbolo, ""))
                        cmd.Parameters.AddWithValue("monto", op.Monto)
                        cmd.Parameters.AddWithValue("dur", If(op.Duracion, ""))
                        cmd.Parameters.AddWithValue("est", If(op.Estado, ""))
                        cmd.Parameters.AddWithValue("pl", op.ProfitLoss)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                End Using
            Catch ex As Exception
                ' Antes era un Catch vacio: un fallo aqui dejaba un contrato real sin registrar.
                AppLogger.Error("[ClientRepository] No se pudo guardar la operacion " & op.ContractId & " en trading_operations", ex)
            End Try
        End Function

        ''' <summary>
        ''' Cierra en trading_operations la operacion con su resultado REAL de Deriv.
        ''' Solo toca filas aun 'Abierto' (idempotente). Antes nunca se actualizaban:
        ''' la tabla solo recibia el INSERT al comprar.
        ''' </summary>
        Public Async Function CerrarOperacionPorContractAsync(contractId As String, estado As String,
                                                               profitLoss As Decimal) As Task(Of Integer)
            If String.IsNullOrEmpty(contractId) Then Return 0
            Using conn As New NpgsqlConnection(_connectionString)
                Await conn.OpenAsync()
                Dim sql = "UPDATE public.trading_operations SET estado = @est, profit_loss = @pl " &
                          "WHERE contract_id = @cid AND estado = 'Abierto'"
                Using cmd As New NpgsqlCommand(sql, conn)
                    cmd.Parameters.AddWithValue("est", estado)
                    cmd.Parameters.AddWithValue("pl", profitLoss)
                    cmd.Parameters.AddWithValue("cid", contractId)
                    Return Await cmd.ExecuteNonQueryAsync()
                End Using
            End Using
        End Function

        ''' <summary>contract_id de las operaciones que siguen 'Abierto' (para reconciliar con Deriv).</summary>
        Public Async Function ObtenerContratosAbiertosAsync(Optional limite As Integer = 500) As Task(Of List(Of String))
            Dim lista As New List(Of String)()
            Using conn As New NpgsqlConnection(_connectionString)
                Await conn.OpenAsync()
                Dim sql = "SELECT contract_id FROM public.trading_operations " &
                          "WHERE estado = 'Abierto' AND COALESCE(contract_id,'') <> '' " &
                          "ORDER BY created_at DESC LIMIT @lim"
                Using cmd As New NpgsqlCommand(sql, conn)
                    cmd.Parameters.AddWithValue("lim", limite)
                    Using reader = Await cmd.ExecuteReaderAsync()
                        While Await reader.ReadAsync()
                            lista.Add(reader.GetString(0))
                        End While
                    End Using
                End Using
            End Using
            Return lista
        End Function

        ''' <summary>
        ''' Borra definitivamente una operacion del repo de cliente. Usado por el boton
        ''' "Quitar" en Trading Live para que la op no vuelva a aparecer al reconectar.
        ''' </summary>
        Public Async Function EliminarOperacionAsync(operacionId As Integer) As Task
            If operacionId <= 0 Then Return
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql = "DELETE FROM public.trading_operations WHERE id = @id;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("id", operacionId)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                End Using
            Catch ex As Exception
                ' silencioso
            End Try
        End Function
    End Class
End Namespace
