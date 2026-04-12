' ============================================================
' EstrategiaRegistry.vb
' Registro centralizado de estrategias de trading.
'
' Patrón idéntico a Indicators/IndicatorRegistry.vb:
'   GetAll()       → lista completa
'   GetByName(s)   → búsqueda por fragmento
'   GetCatalog()   → lista de JObject para HTTP/MCP
'
' Para agregar una nueva estrategia: añadirla en GetAll().
' ============================================================
Imports System.Collections.Generic
Imports System.Linq
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB.Estrategias

    Public Module EstrategiaRegistry

        ''' <summary>
        ''' Devuelve todas las estrategias registradas con parámetros por defecto.
        ''' Agregar nuevas estrategias aquí.
        ''' </summary>
        Public Function GetAll() As List(Of IEstrategia)
            Return New List(Of IEstrategia) From {
                New EstrategiaEmaRsi(),
                New EstrategiaSeykota(20)
            }
        End Function

        ''' <summary>
        ''' Busca una estrategia por nombre o fragmento (case-insensitive).
        ''' Ej: "EmaRsi", "ema", "Seykota", "seyk"
        ''' </summary>
        Public Function GetByName(nombre As String) As IEstrategia
            If String.IsNullOrWhiteSpace(nombre) Then Return Nothing
            Dim n = nombre.Trim().ToLowerInvariant()
            Return GetAll().FirstOrDefault(
                Function(e) e.Nombre.ToLowerInvariant().Contains(n))
        End Function

        ''' <summary>
        ''' Catálogo serializable a JSON para el endpoint /api/estrategias.
        ''' </summary>
        Public Function GetCatalog() As JArray
            Dim arr As New JArray()
            For Each e In GetAll()
                arr.Add(New JObject From {
                    {"nombre",      e.Nombre},
                    {"descripcion", e.Descripcion}
                })
            Next
            Return arr
        End Function

    End Module

End Namespace
