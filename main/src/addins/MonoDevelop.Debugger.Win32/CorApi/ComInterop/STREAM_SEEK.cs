﻿using System.IO;

namespace CorApi.ComInterop
{
  /// <summary>
  /// The STREAM_SEEK enumeration values specify the origin from which to calculate the new seek-pointer location. They are used for the dworigin parameter in the IStream::Seek method. The new seek position is calculated using this value and the dlibMove parameter.
  /// </summary>
  /// <remarks>Binary compatible with the <see cref="SeekOrigin" /> enum.</remarks>
  /// <example><code>
  ///     typedef enum tagSTREAM_SEEK
  ///    {
  ///        STREAM_SEEK_SET = 0,
  ///        STREAM_SEEK_CUR = 1,
  ///        STREAM_SEEK_END = 2
  ///    } STREAM_SEEK;
  /// </code></example>
  public enum STREAM_SEEK : uint
  {
    /// <summary>
    /// The new seek pointer is an offset relative to the beginning of the stream. In this case, the dlibMove parameter is the new seek position relative to the beginning of the stream.
    /// </summary>
    STREAM_SEEK_SET = 0,

    /// <summary>
    /// The new seek pointer is an offset relative to the current seek pointer location. In this case, the dlibMove parameter is the signed displacement from the current seek position.
    /// </summary>
    STREAM_SEEK_CUR = 1,

    /// <summary>
    /// The new seek pointer is an offset relative to the end of the stream. In this case, the dlibMove parameter is the new seek position relative to the end of the stream.
    /// </summary>
    STREAM_SEEK_END = 2
  };
}