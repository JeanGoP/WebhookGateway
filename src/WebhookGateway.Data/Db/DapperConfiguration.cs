using System.Data;
using Dapper;

namespace WebhookGateway.Data.Db;

/// <summary>
/// Ajustes globales de Dapper. Se aplican una sola vez, al registrar el acceso a datos.
/// </summary>
/// <remarks>
/// Por defecto Dapper manda cualquier <see cref="DateTime"/> como <c>DbType.DateTime</c>, y
/// SqlClient lo traduce al tipo <c>datetime</c> de SQL Server: una rejilla de 1/300 de segundo,
/// no de milisegundos. Todas las columnas temporales del esquema son <c>datetime2(3)</c>, y al
/// comparar los dos tipos SQL Server convierte el <c>datetime</c> a <c>datetime2</c> con su
/// precisión real. Así, 14:10:18.733 viaja como <c>.7333333</c> y no coincide con el
/// <c>.7330000</c> que hay guardado: la consulta devuelve cero filas sin error ninguno. Solo se
/// salvan los milisegundos múltiplos de diez, que son los únicos tics que convierten exactos.
/// <para>
/// Con <c>DbType.DateTime2</c> el valor viaja tal cual y la comparación es exacta. Es seguro
/// hacerlo global porque en el esquema no queda ni una columna <c>datetime</c> ni
/// <c>smalldatetime</c>: si algún día se añade una, esto hay que revisarlo.
/// </para>
/// </remarks>
public static class DapperConfiguration
{
    private static readonly object Gate = new();

    private static bool _applied;

    public static void Apply()
    {
        lock (Gate)
        {
            if (_applied)
            {
                return;
            }

            SqlMapper.AddTypeMap(typeof(DateTime), DbType.DateTime2);
            SqlMapper.AddTypeMap(typeof(DateTime?), DbType.DateTime2);

            _applied = true;
        }
    }
}
