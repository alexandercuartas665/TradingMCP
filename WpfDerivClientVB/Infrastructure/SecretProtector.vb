' ============================================================
' SecretProtector.vb
'
' Cifra secretos (tokens de Deriv) antes de guardarlos en la BD
' usando DPAPI de Windows (ProtectedData, ambito CurrentUser).
'
' - Solo el mismo usuario de Windows, en el mismo PC, puede
'   descifrar. Un volcado de la BD o una copia del backup no
'   expone los tokens.
' - Formato guardado: "dpapi:" + Base64(blob).
' - Unprotect acepta valores antiguos en texto plano (sin prefijo)
'   y los devuelve tal cual, para no romper la transicion.
' ============================================================
Imports System.Security.Cryptography
Imports System.Text

Namespace WpfDerivClientVB

    Public Module SecretProtector

        Public Const Prefijo As String = "dpapi:"

        ' Entropia adicional propia de la app: otro programa del mismo
        ' usuario no puede descifrar sin conocer este valor.
        Private ReadOnly Entropia As Byte() = Encoding.UTF8.GetBytes("WpfDerivClientVB.tokens.v1")

        ''' <summary>True si el valor ya esta cifrado con este modulo.</summary>
        Public Function EstaCifrado(valor As String) As Boolean
            Return Not String.IsNullOrEmpty(valor) AndAlso valor.StartsWith(Prefijo, StringComparison.Ordinal)
        End Function

        ''' <summary>Cifra un texto. Vacio/Nothing se devuelve vacio; lo ya cifrado no se re-cifra.</summary>
        Public Function Protect(texto As String) As String
            If String.IsNullOrEmpty(texto) Then Return ""
            If EstaCifrado(texto) Then Return texto
            Dim datos = Encoding.UTF8.GetBytes(texto)
            Dim blob = ProtectedData.Protect(datos, Entropia, DataProtectionScope.CurrentUser)
            Return Prefijo & Convert.ToBase64String(blob)
        End Function

        ''' <summary>
        ''' Descifra un valor guardado. Texto plano antiguo se devuelve tal cual.
        ''' Si no se puede descifrar (otro usuario/PC) devuelve "" y lo registra.
        ''' </summary>
        Public Function Unprotect(guardado As String) As String
            If String.IsNullOrEmpty(guardado) Then Return ""
            If Not EstaCifrado(guardado) Then Return guardado
            Try
                Dim blob = Convert.FromBase64String(guardado.Substring(Prefijo.Length))
                Dim datos = ProtectedData.Unprotect(blob, Entropia, DataProtectionScope.CurrentUser)
                Return Encoding.UTF8.GetString(datos)
            Catch ex As Exception
                AppLogger.Warn("[SecretProtector] No se pudo descifrar un secreto (otro usuario de Windows u otro PC?): " & ex.Message)
                Return ""
            End Try
        End Function

    End Module

End Namespace
