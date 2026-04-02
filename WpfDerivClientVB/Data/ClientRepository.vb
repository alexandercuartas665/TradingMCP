Imports System.Collections.Generic
Imports System.Threading.Tasks
Imports Npgsql
Imports WpfDerivClientVB.Infrastructure

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
                            app_id_real VARCHAR(50),
                            api_token_real VARCHAR(100),
                            app_id_virtual VARCHAR(50),
                            api_token_virtual VARCHAR(100),
                            UNIQUE(client_id)
                        );"

                    Using cmd As New NpgsqlCommand(sqlClients, conn)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                    Using cmd As New NpgsqlCommand(sqlCreds, conn)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                End Using
            Catch ex As Exception
                Throw New Exception("Error al inicializar tablas: " & ex.Message)
            End Try
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
                                Return New ClientDerivCredentials With {
                                    .AppIdReal = reader("app_id_real").ToString(),
                                    .ApiTokenReal = reader("api_token_real").ToString(),
                                    .AppIdVirtual = reader("app_id_virtual").ToString(),
                                    .ApiTokenVirtual = reader("api_token_virtual").ToString()
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
                                Dim credsSql = "INSERT INTO public.client_deriv_credentials (client_id, app_id_real, api_token_real, app_id_virtual, api_token_virtual) " &
                                             "VALUES (@cid, @air, @atr, @aiv, @atv) " &
                                             "ON CONFLICT (client_id) DO UPDATE SET " &
                                             "app_id_real = EXCLUDED.app_id_real, api_token_real = EXCLUDED.api_token_real, " &
                                             "app_id_virtual = EXCLUDED.app_id_virtual, api_token_virtual = EXCLUDED.api_token_virtual"
                                
                                Using cmdCreds = New NpgsqlCommand(credsSql, conn, trans)
                                    cmdCreds.Parameters.AddWithValue("cid", clientId)
                                    cmdCreds.Parameters.AddWithValue("air", If(client.DerivCredentials.AppIdReal, ""))
                                    cmdCreds.Parameters.AddWithValue("atr", If(client.DerivCredentials.ApiTokenReal, ""))
                                    cmdCreds.Parameters.AddWithValue("aiv", If(client.DerivCredentials.AppIdVirtual, ""))
                                    cmdCreds.Parameters.AddWithValue("atv", If(client.DerivCredentials.ApiTokenVirtual, ""))
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
    End Class
End Namespace
