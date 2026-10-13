///////////////////////////////////////////////////////////////////////////////
//
// (C) 2026 ICE TEA GROUP LLC - ALL RIGHTS RESERVED
//
// 
//
// ALL INFORMATION CONTAINED HEREIN IS, AND REMAINS
// THE PROPERTY OF ICE TEA GROUP LLC AND ITS SUPPLIERS, IF ANY.
// THE INTELLECTUAL PROPERTY AND TECHNICAL CONCEPTS CONTAINED
// HEREIN ARE PROPRIETARY TO ICE TEA GROUP LLC AND ITS SUPPLIERS
// AND MAY BE COVERED BY U.S. AND FOREIGN PATENTS, PATENT IN PROCESS, AND
// ARE PROTECTED BY TRADE SECRET OR COPYRIGHT LAW.
//
// DISSEMINATION OF THIS INFORMATION OR REPRODUCTION OF THIS MATERIAL
// IS STRICTLY FORBIDDEN UNLESS PRIOR WRITTEN PERMISSION IS OBTAINED
// FROM ICE TEA GROUP LLC.
//
///////////////////////////////////////////////////////////////////////////////

// Standard security handler (ISO 32000-2, 7.6.4): revisions 2 to 6,
// RC4 40/128-bit, AES-128 and AES-256.

using System;
using System.Security.Cryptography;
using System.Text;
using Wisej.AI.Pdf.Objects;

namespace Wisej.AI.Pdf.Security
{
	/// <summary>
	/// Decrypts strings and streams of documents protected with the standard security handler.
	/// </summary>
	/// <remarks>
	/// Content extraction only needs read access, so both the user and the owner password are
	/// accepted, and documents with an empty user password open without a password.
	/// Permission flags are not enforced.
	/// </remarks>
	internal sealed class StandardDecryptor
	{
		#region Nested types

		/// <summary>
		/// The cipher used by a crypt filter.
		/// </summary>
		private enum CipherMethod
		{
			/// <summary>No encryption (the Identity crypt filter).</summary>
			None,

			/// <summary>RC4 with a key derived per object.</summary>
			RC4,

			/// <summary>AES-128 CBC with a key derived per object.</summary>
			AesV2,

			/// <summary>AES-256 CBC with the file key.</summary>
			AesV3,
		}

		#endregion

		// the 32-byte padding string from the specification (Algorithm 2).
		private static readonly byte[] Padding =
		{
			0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
			0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
		};

		// the file encryption key: 5 to 16 bytes for RC4 and AESV2, 32 bytes for AESV3; empty when nothing is encrypted.
		private byte[] _key;

		// cipher used for streams (/StmF).
		private CipherMethod _streamMethod;

		// cipher used for strings (/StrF).
		private CipherMethod _stringMethod;

		// false when the XMP metadata stream is stored in clear text (/EncryptMetadata false).
		private bool _encryptMetadata = true;

		/// <summary>
		/// Initializes a new instance of <see cref="StandardDecryptor"/>; use <see cref="Create"/> to obtain one.
		/// </summary>
		private StandardDecryptor()
		{
		}

		/// <summary>
		/// The reference of the <c>/Encrypt</c> dictionary, whose strings are never encrypted.
		/// </summary>
		public PdfReference ExemptReference { get; set; }

		#region Factory

		/// <summary>
		/// Creates a decryptor for the given encryption dictionary and authenticates <paramref name="password"/>.
		/// </summary>
		/// <param name="encrypt">The <c>/Encrypt</c> dictionary from the trailer.</param>
		/// <param name="documentId">The first element of the trailer <c>/ID</c> array.</param>
		/// <param name="password">The user or owner password; null or empty to try the empty user password.</param>
		/// <returns>The decryptor.</returns>
		/// <exception cref="PdfException">The security handler is not supported.</exception>
		/// <exception cref="PdfPasswordException">The password is wrong.</exception>
		public static StandardDecryptor Create(PdfDictionary encrypt, byte[] documentId, string password)
		{
			var filter = encrypt.GetNameValue(PdfNames.Filter);
			if (filter != null && filter != "Standard")
				throw new PdfException($"Unsupported security handler: {filter}.");

			var decryptor = new StandardDecryptor();

			var v = encrypt.GetInt("V");
			var r = encrypt.GetInt("R", 2);
			var o = encrypt.GetString("O")?.Bytes ?? Array.Empty<byte>();
			var u = encrypt.GetString("U")?.Bytes ?? Array.Empty<byte>();
			var p = encrypt.GetInt("P");
			var keyLength = encrypt.GetInt(PdfNames.Length, 40) / 8;
			decryptor._encryptMetadata = encrypt.GetBool("EncryptMetadata", true);

			// key lengths are sometimes given in bytes instead of bits.
			if (keyLength < 5)
				keyLength = encrypt.GetInt(PdfNames.Length, 5);

			keyLength = Math.Max(5, Math.Min(16, keyLength));

			// determine the ciphers.
			switch (v)
			{
				case 1:
					keyLength = 5;
					decryptor._streamMethod = decryptor._stringMethod = CipherMethod.RC4;
					break;

				case 2:
				case 3:
					decryptor._streamMethod = decryptor._stringMethod = CipherMethod.RC4;
					break;

				case 4:
				case 5:
					decryptor._streamMethod = GetCryptFilterMethod(encrypt, encrypt.GetNameValue("StmF"), ref keyLength);
					decryptor._stringMethod = GetCryptFilterMethod(encrypt, encrypt.GetNameValue("StrF"), ref keyLength);
					break;

				default:
					decryptor._streamMethod = decryptor._stringMethod = CipherMethod.RC4;
					break;
			}

			// only embedded files are encrypted (AuthEvent /EFOpen): the document itself opens without a password.
			if (decryptor._streamMethod == CipherMethod.None && decryptor._stringMethod == CipherMethod.None)
			{
				decryptor._key = Array.Empty<byte>();
				return decryptor;
			}

			var passwordBytes = password ?? string.Empty;

			if (r >= 5)
			{
				var oe = encrypt.GetString("OE")?.Bytes ?? Array.Empty<byte>();
				var ue = encrypt.GetString("UE")?.Bytes ?? Array.Empty<byte>();
				decryptor._key = AuthenticateAes256(r, passwordBytes, o, u, oe, ue);
			}
			else
			{
				decryptor._key = AuthenticateLegacy(r, keyLength, passwordBytes, o, u, p, documentId, decryptor._encryptMetadata);
			}

			if (decryptor._key == null)
				throw new PdfPasswordException(string.IsNullOrEmpty(password)
					? "The document is encrypted and requires a password."
					: "The password is not correct.");

			return decryptor;
		}

		/// <summary>
		/// Resolves the cipher of a named crypt filter from the <c>/CF</c> dictionary (ISO 32000-2, 7.6.6).
		/// </summary>
		/// <param name="encrypt">The <c>/Encrypt</c> dictionary.</param>
		/// <param name="filterName">The value of <c>/StmF</c> or <c>/StrF</c>; null when missing.</param>
		/// <param name="keyLength">The key length in bytes; updated from the crypt filter's <c>/CFM</c> and <c>/Length</c>.</param>
		/// <returns>The cipher method; RC4 when the method is unknown.</returns>
		private static CipherMethod GetCryptFilterMethod(PdfDictionary encrypt, string filterName, ref int keyLength)
		{
			// a missing /StmF or /StrF means Identity (ISO 32000-2, table 20).
			if (filterName == null || filterName == "Identity")
				return CipherMethod.None;

			var filter = encrypt.GetDictionary("CF")?.GetDictionary(filterName);
			var method = filter?.GetNameValue("CFM");

			switch (method)
			{
				case "AESV2":
					keyLength = 16;
					return CipherMethod.AesV2;

				case "AESV3":
					keyLength = 32;
					return CipherMethod.AesV3;

				case "None":
					return CipherMethod.None;

				default:
					var length = filter?.GetInt(PdfNames.Length, 0) ?? 0;
					if (length > 0)
						keyLength = length > 16 ? length / 8 : length;

					return CipherMethod.RC4;
			}
		}

		#endregion

		#region Decryption

		/// <summary>
		/// Decrypts a string that belongs to the object <paramref name="owner"/>.
		/// </summary>
		/// <param name="data">The encrypted bytes.</param>
		/// <param name="owner">The indirect object that contains the string.</param>
		/// <returns>The decrypted bytes.</returns>
		public byte[] DecryptString(byte[] data, PdfReference owner)
		{
			if (data.Length == 0 || owner == null || owner.Equals(this.ExemptReference))
				return data;

			return Decrypt(data, owner, _stringMethod);
		}

		/// <summary>
		/// Decrypts the raw data of <paramref name="stream"/>.
		/// </summary>
		/// <param name="stream">The stream to decrypt.</param>
		/// <param name="owner">The indirect object that holds the stream.</param>
		/// <returns>The decrypted bytes.</returns>
		public byte[] DecryptStream(PdfStream stream, PdfReference owner)
		{
			var data = stream.RawData;
			if (data == null || data.Length == 0 || owner == null)
				return data;

			// cross-reference streams are never encrypted.
			if (stream.IsType("XRef"))
				return data;

			// the metadata stream is in clear text when /EncryptMetadata is false.
			if (!_encryptMetadata && stream.IsType("Metadata"))
				return data;

			// streams that specify the Identity crypt filter are not encrypted.
			if (HasIdentityCryptFilter(stream))
				return data;

			return Decrypt(data, owner, _streamMethod);
		}

		/// <summary>
		/// Returns true when the first filter of <paramref name="stream"/> is <c>/Crypt</c>, meaning the stream
		/// specifies its own crypt filter (ISO 32000-2, 7.4.10) and is not decrypted with the default one.
		/// </summary>
		/// <param name="stream">The stream to test.</param>
		/// <returns>True when the stream starts with a <c>/Crypt</c> filter.</returns>
		private static bool HasIdentityCryptFilter(PdfStream stream)
		{
			var filter = stream.Resolve(PdfNames.Filter);
			if (filter is PdfName name)
				return name.Value == "Crypt";

			if (filter is PdfArray array && array.Count > 0)
				return array.GetName(0)?.Value == "Crypt";

			return false;
		}

		/// <summary>
		/// Decrypts <paramref name="data"/> with the given cipher and the key of the owning object.
		/// </summary>
		/// <param name="data">The encrypted bytes.</param>
		/// <param name="owner">The indirect object that contains the data.</param>
		/// <param name="method">The cipher to use.</param>
		/// <returns>The decrypted bytes, or <paramref name="data"/> unchanged when it can't be decrypted.</returns>
		private byte[] Decrypt(byte[] data, PdfReference owner, CipherMethod method)
		{
			try
			{
				switch (method)
				{
					case CipherMethod.RC4:
						return Rc4(ObjectKey(owner, false), data);

					case CipherMethod.AesV2:
						return AesCbcDecrypt(ObjectKey(owner, true), data);

					case CipherMethod.AesV3:
						return AesCbcDecrypt(_key, data);

					default:
						return data;
				}
			}
			catch (CryptographicException)
			{
				// leave undecryptable data as is; the content parser will cope with garbage.
				return data;
			}
		}

		/// <summary>
		/// Computes the per-object key (ISO 32000-2, 7.6.3.3, Algorithm 1):
		/// MD5(file key + object number + generation [+ "sAlT"]), truncated to n + 5 bytes (max 16).
		/// </summary>
		/// <param name="owner">The indirect object.</param>
		/// <param name="aes">True to append the "sAlT" suffix used by AESV2.</param>
		/// <returns>The object key.</returns>
		private byte[] ObjectKey(PdfReference owner, bool aes)
		{
			var input = new byte[_key.Length + 5 + (aes ? 4 : 0)];
			Buffer.BlockCopy(_key, 0, input, 0, _key.Length);

			var n = _key.Length;
			input[n] = (byte)owner.Number;
			input[n + 1] = (byte)(owner.Number >> 8);
			input[n + 2] = (byte)(owner.Number >> 16);
			input[n + 3] = (byte)owner.Generation;
			input[n + 4] = (byte)(owner.Generation >> 8);

			if (aes)
			{
				input[n + 5] = 0x73;
				input[n + 6] = 0x41;
				input[n + 7] = 0x6C;
				input[n + 8] = 0x54;
			}

			using (var md5 = MD5.Create())
			{
				var hash = md5.ComputeHash(input);
				var length = Math.Min(_key.Length + 5, 16);
				var key = new byte[length];
				Buffer.BlockCopy(hash, 0, key, 0, length);
				return key;
			}
		}

		#endregion

		#region Authentication: revisions 2-4

		/// <summary>
		/// Authenticates a password for revisions 2 to 4, first as the user password and then as the owner password.
		/// </summary>
		/// <param name="r">The revision (<c>/R</c>).</param>
		/// <param name="keyLength">The key length in bytes.</param>
		/// <param name="password">The password to test.</param>
		/// <param name="o">The <c>/O</c> entry.</param>
		/// <param name="u">The <c>/U</c> entry.</param>
		/// <param name="p">The permission flags (<c>/P</c>).</param>
		/// <param name="id">The first element of the trailer <c>/ID</c>.</param>
		/// <param name="encryptMetadata">The <c>/EncryptMetadata</c> flag.</param>
		/// <returns>The file encryption key, or null when the password is wrong.</returns>
		private static byte[] AuthenticateLegacy(int r, int keyLength, string password, byte[] o, byte[] u, int p, byte[] id, bool encryptMetadata)
		{
			var passwordBytes = Latin1Bytes(password);

			// try the password as the user password.
			var key = ComputeFileKey(r, keyLength, passwordBytes, o, p, id, encryptMetadata);
			if (CheckUserKey(r, key, u, id))
				return key;

			// try the password as the owner password: recover the user password from /O.
			var userPassword = RecoverUserPassword(r, keyLength, passwordBytes, o);
			key = ComputeFileKey(r, keyLength, userPassword, o, p, id, encryptMetadata);
			if (CheckUserKey(r, key, u, id))
				return key;

			return null;
		}

		/// <summary>
		/// Computes the file encryption key from a user password (ISO 32000-2, 7.6.4.3.2, Algorithm 2).
		/// </summary>
		/// <param name="r">The revision (<c>/R</c>).</param>
		/// <param name="keyLength">The key length in bytes; revision 2 always uses 5.</param>
		/// <param name="password">The user password bytes, unpadded.</param>
		/// <param name="o">The <c>/O</c> entry.</param>
		/// <param name="p">The permission flags (<c>/P</c>).</param>
		/// <param name="id">The first element of the trailer <c>/ID</c>.</param>
		/// <param name="encryptMetadata">The <c>/EncryptMetadata</c> flag (revision 4 and later).</param>
		/// <returns>The file encryption key.</returns>
		private static byte[] ComputeFileKey(int r, int keyLength, byte[] password, byte[] o, int p, byte[] id, bool encryptMetadata)
		{
			using (var md5 = MD5.Create())
			{
				var input = new System.IO.MemoryStream();
				var padded = PadPassword(password);
				input.Write(padded, 0, 32);
				input.Write(o, 0, Math.Min(32, o.Length));
				input.WriteByte((byte)p);
				input.WriteByte((byte)(p >> 8));
				input.WriteByte((byte)(p >> 16));
				input.WriteByte((byte)(p >> 24));
				input.Write(id, 0, id.Length);

				if (r >= 4 && !encryptMetadata)
				{
					for (int i = 0; i < 4; i++)
						input.WriteByte(0xFF);
				}

				var hash = md5.ComputeHash(input.ToArray());
				if (r >= 3)
				{
					for (int i = 0; i < 50; i++)
						hash = md5.ComputeHash(hash, 0, keyLength);
				}

				var key = new byte[r == 2 ? 5 : Math.Min(16, keyLength)];
				Buffer.BlockCopy(hash, 0, key, 0, key.Length);
				return key;
			}
		}

		/// <summary>
		/// Verifies a candidate file key by recomputing <c>/U</c> (ISO 32000-2, Algorithms 4 and 5).
		/// </summary>
		/// <param name="r">The revision (<c>/R</c>).</param>
		/// <param name="key">The candidate file encryption key.</param>
		/// <param name="u">The <c>/U</c> entry.</param>
		/// <param name="id">The first element of the trailer <c>/ID</c>.</param>
		/// <returns>True when the key matches: all 32 bytes for revision 2, the first 16 bytes otherwise.</returns>
		private static bool CheckUserKey(int r, byte[] key, byte[] u, byte[] id)
		{
			if (r == 2)
			{
				var expected = Rc4(key, Padding);
				return StartsWith(u, expected, 32);
			}

			using (var md5 = MD5.Create())
			{
				var input = new byte[32 + id.Length];
				Buffer.BlockCopy(Padding, 0, input, 0, 32);
				Buffer.BlockCopy(id, 0, input, 32, id.Length);

				var value = Rc4(key, md5.ComputeHash(input));
				var stepKey = new byte[key.Length];
				for (int i = 1; i <= 19; i++)
				{
					for (int j = 0; j < key.Length; j++)
						stepKey[j] = (byte)(key[j] ^ i);

					value = Rc4(stepKey, value);
				}

				return StartsWith(u, value, 16);
			}
		}

		/// <summary>
		/// Recovers the padded user password by decrypting <c>/O</c> with the key derived from the owner password
		/// (ISO 32000-2, Algorithm 7, the inverse of Algorithm 3).
		/// </summary>
		/// <param name="r">The revision (<c>/R</c>).</param>
		/// <param name="keyLength">The key length in bytes; revision 2 always uses 5.</param>
		/// <param name="ownerPassword">The candidate owner password bytes.</param>
		/// <param name="o">The <c>/O</c> entry.</param>
		/// <returns>The padded user password (32 bytes when <c>/O</c> is well formed).</returns>
		private static byte[] RecoverUserPassword(int r, int keyLength, byte[] ownerPassword, byte[] o)
		{
			using (var md5 = MD5.Create())
			{
				var hash = md5.ComputeHash(PadPassword(ownerPassword));
				if (r >= 3)
				{
					for (int i = 0; i < 50; i++)
						hash = md5.ComputeHash(hash);
				}

				var key = new byte[r == 2 ? 5 : Math.Min(16, keyLength)];
				Buffer.BlockCopy(hash, 0, key, 0, key.Length);

				var value = new byte[Math.Min(32, o.Length)];
				Buffer.BlockCopy(o, 0, value, 0, value.Length);

				if (r == 2)
					return Rc4(key, value);

				var stepKey = new byte[key.Length];
				for (int i = 19; i >= 0; i--)
				{
					for (int j = 0; j < key.Length; j++)
						stepKey[j] = (byte)(key[j] ^ i);

					value = Rc4(stepKey, value);
				}

				return value;
			}
		}

		/// <summary>
		/// Pads or truncates a password to 32 bytes using <see cref="Padding"/> (Algorithm 2, step a).
		/// </summary>
		/// <param name="password">The password bytes.</param>
		/// <returns>A new 32-byte array.</returns>
		private static byte[] PadPassword(byte[] password)
		{
			var padded = new byte[32];
			var length = Math.Min(32, password.Length);
			Buffer.BlockCopy(password, 0, padded, 0, length);
			Buffer.BlockCopy(Padding, 0, padded, length, 32 - length);
			return padded;
		}

		#endregion

		#region Authentication: revisions 5-6 (AES-256)

		/// <summary>
		/// Authenticates a password for revisions 5 and 6 (ISO 32000-2, Algorithms 2.A, 11 and 12),
		/// first as the owner password and then as the user password.
		/// </summary>
		/// <param name="r">The revision (<c>/R</c>).</param>
		/// <param name="password">The password to test.</param>
		/// <param name="o">The 48-byte <c>/O</c> entry: hash, validation salt and key salt.</param>
		/// <param name="u">The 48-byte <c>/U</c> entry: hash, validation salt and key salt.</param>
		/// <param name="oe">The <c>/OE</c> entry: the file key encrypted with the owner key.</param>
		/// <param name="ue">The <c>/UE</c> entry: the file key encrypted with the user key.</param>
		/// <returns>The 32-byte file encryption key, or null when the password is wrong.</returns>
		private static byte[] AuthenticateAes256(int r, string password, byte[] o, byte[] u, byte[] oe, byte[] ue)
		{
			if (o.Length < 48 || u.Length < 48)
				return null;

			// passwords are prepared with SASLprep, encoded in UTF-8 and truncated to 127 bytes.
			var passwordBytes = Encoding.UTF8.GetBytes(SaslPrep(password));
			if (passwordBytes.Length > 127)
				Array.Resize(ref passwordBytes, 127);

			var u48 = Slice(u, 0, 48);

			// owner password: hash(password + O validation salt + U).
			var ownerHash = Hash(r, passwordBytes, Slice(o, 32, 8), u48);
			if (StartsWith(o, ownerHash, 32))
			{
				var intermediate = Hash(r, passwordBytes, Slice(o, 40, 8), u48);
				return AesNoPaddingDecrypt(intermediate, oe);
			}

			// user password: hash(password + U validation salt).
			var userHash = Hash(r, passwordBytes, Slice(u, 32, 8), Array.Empty<byte>());
			if (StartsWith(u, userHash, 32))
			{
				var intermediate = Hash(r, passwordBytes, Slice(u, 40, 8), Array.Empty<byte>());
				return AesNoPaddingDecrypt(intermediate, ue);
			}

			return null;
		}

		/// <summary>
		/// Computes the password hash of revision 5 (a single SHA-256) or revision 6
		/// (ISO 32000-2, Algorithm 2.B: iterated SHA-256/384/512 with AES-128).
		/// </summary>
		/// <param name="r">The revision (<c>/R</c>).</param>
		/// <param name="password">The UTF-8 password, at most 127 bytes.</param>
		/// <param name="salt">The 8-byte validation or key salt.</param>
		/// <param name="userData">The 48-byte <c>/U</c> entry when hashing an owner password; empty otherwise.</param>
		/// <returns>The 32-byte hash.</returns>
		private static byte[] Hash(int r, byte[] password, byte[] salt, byte[] userData)
		{
			byte[] k;
			using (var sha256 = SHA256.Create())
			{
				k = sha256.ComputeHash(Concat(password, salt, userData));
				if (r < 6)
					return k;

				using (var sha384 = SHA384.Create())
				using (var sha512 = SHA512.Create())
				using (var aes = Aes.Create())
				{
					aes.Mode = CipherMode.CBC;
					aes.Padding = PaddingMode.None;

					for (int round = 0; ; round++)
					{
						// K1 = (password + K + userData) repeated 64 times.
						var block = Concat(password, k, userData);
						var k1 = new byte[block.Length * 64];
						for (int i = 0; i < 64; i++)
							Buffer.BlockCopy(block, 0, k1, i * block.Length, block.Length);

						byte[] e;
						using (var encryptor = aes.CreateEncryptor(Slice(k, 0, 16), Slice(k, 16, 16)))
							e = encryptor.TransformFinalBlock(k1, 0, k1.Length);

						// the sum of the first 16 bytes modulo 3 selects the next hash function.
						var sum = 0;
						for (int i = 0; i < 16; i++)
							sum += e[i];

						switch (sum % 3)
						{
							case 0:
								k = sha256.ComputeHash(e);
								break;

							case 1:
								k = sha384.ComputeHash(e);
								break;

							default:
								k = sha512.ComputeHash(e);
								break;
						}

						if (round >= 63 && e[e.Length - 1] <= round - 31)
							break;
					}
				}
			}

			return Slice(k, 0, 32);
		}

		/// <summary>
		/// Decrypts the first 32 bytes of <c>/OE</c> or <c>/UE</c> with AES-256 CBC, a zero IV and no padding.
		/// </summary>
		/// <param name="key">The 32-byte intermediate key.</param>
		/// <param name="data">The encrypted file key.</param>
		/// <returns>The 32-byte file encryption key, or null when <paramref name="data"/> is shorter than 32 bytes.</returns>
		private static byte[] AesNoPaddingDecrypt(byte[] key, byte[] data)
		{
			if (data.Length < 32)
				return null;

			using (var aes = Aes.Create())
			{
				aes.Mode = CipherMode.CBC;
				aes.Padding = PaddingMode.None;
				using (var decryptor = aes.CreateDecryptor(key, new byte[16]))
					return decryptor.TransformFinalBlock(data, 0, 32);
			}
		}

		/// <summary>
		/// Prepares a password with the SASLprep profile of stringprep (RFC 4013), as required by revision 6.
		/// </summary>
		/// <param name="password">The password.</param>
		/// <returns>The prepared password.</returns>
		/// <remarks>
		/// Implements the mapping and normalization steps: characters commonly mapped to nothing are removed,
		/// non-ASCII spaces become ASCII spaces, and the result is normalized with NFKC. The prohibited
		/// character checks are not needed to authenticate.
		/// </remarks>
		private static string SaslPrep(string password)
		{
			var sb = new StringBuilder(password.Length);
			foreach (var c in password)
			{
				switch (c)
				{
					// RFC 3454 table B.1: commonly mapped to nothing.
					case '\u00AD':
					case '\u034F':
					case '\u1806':
					case '\u180B':
					case '\u180C':
					case '\u180D':
					case '\u200B':
					case '\u200C':
					case '\u200D':
					case '\u2060':
					case '\uFEFF':
						continue;

					// RFC 3454 table C.1.2: non-ASCII spaces map to SPACE.
					case '\u00A0':
					case '\u1680':
					case '\u202F':
					case '\u205F':
					case '\u3000':
						sb.Append(' ');
						continue;

					default:
						// variation selectors are also mapped to nothing.
						if (c >= '\uFE00' && c <= '\uFE0F')
							continue;

						if (c >= '\u2000' && c <= '\u200A')
						{
							sb.Append(' ');
							continue;
						}

						sb.Append(c);
						break;
				}
			}

			return sb.ToString().Normalize(NormalizationForm.FormKC);
		}

		#endregion

		#region Ciphers

		/// <summary>
		/// Decrypts AES-CBC data whose first 16 bytes are the initialization vector.
		/// </summary>
		/// <param name="key">The AES key (16 or 32 bytes).</param>
		/// <param name="data">The IV followed by the cipher text.</param>
		/// <returns>The plain text, with the PKCS#7 padding removed when valid.</returns>
		private static byte[] AesCbcDecrypt(byte[] key, byte[] data)
		{
			if (data.Length < 16)
				return Array.Empty<byte>();

			// truncated cipher text: decrypt only the complete blocks.
			var length = (data.Length - 16) / 16 * 16;
			if (length == 0)
				return Array.Empty<byte>();

			byte[] plain;
			using (var aes = Aes.Create())
			{
				aes.Mode = CipherMode.CBC;
				aes.Padding = PaddingMode.None;
				using (var decryptor = aes.CreateDecryptor(key, Slice(data, 0, 16)))
					plain = decryptor.TransformFinalBlock(data, 16, length);
			}

			// remove the PKCS#7 padding manually: some writers produce invalid padding.
			var pad = plain[plain.Length - 1];
			if (pad >= 1 && pad <= 16 && pad <= plain.Length)
			{
				var valid = true;
				for (int i = plain.Length - pad; i < plain.Length; i++)
				{
					if (plain[i] != pad)
					{
						valid = false;
						break;
					}
				}

				if (valid)
					Array.Resize(ref plain, plain.Length - pad);
			}

			return plain;
		}

		/// <summary>
		/// RC4 stream cipher (encryption and decryption are the same operation).
		/// </summary>
		/// <param name="key">The RC4 key.</param>
		/// <param name="data">The input bytes.</param>
		/// <returns>The transformed bytes.</returns>
		private static byte[] Rc4(byte[] key, byte[] data)
		{
			var s = new byte[256];
			for (int i = 0; i < 256; i++)
				s[i] = (byte)i;

			for (int i = 0, j = 0; i < 256; i++)
			{
				j = (j + s[i] + key[i % key.Length]) & 0xFF;
				var t = s[i];
				s[i] = s[j];
				s[j] = t;
			}

			var result = new byte[data.Length];
			for (int n = 0, i = 0, j = 0; n < data.Length; n++)
			{
				i = (i + 1) & 0xFF;
				j = (j + s[i]) & 0xFF;
				var t = s[i];
				s[i] = s[j];
				s[j] = t;
				result[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
			}

			return result;
		}

		#endregion

		#region Helpers

		/// <summary>
		/// Encodes a password for revisions 2 to 4: one byte per character, '?' for characters above U+00FF.
		/// </summary>
		/// <param name="text">The password.</param>
		/// <returns>The Latin-1 bytes.</returns>
		private static byte[] Latin1Bytes(string text)
		{
			var bytes = new byte[text.Length];
			for (int i = 0; i < text.Length; i++)
				bytes[i] = text[i] < 256 ? (byte)text[i] : (byte)'?';

			return bytes;
		}

		/// <summary>
		/// Compares the first <paramref name="length"/> bytes of two arrays.
		/// </summary>
		/// <param name="data">The first array.</param>
		/// <param name="prefix">The second array.</param>
		/// <param name="length">The number of bytes to compare.</param>
		/// <returns>True when both arrays have at least <paramref name="length"/> bytes and they are equal.</returns>
		private static bool StartsWith(byte[] data, byte[] prefix, int length)
		{
			if (data.Length < length || prefix.Length < length)
				return false;

			for (int i = 0; i < length; i++)
			{
				if (data[i] != prefix[i])
					return false;
			}

			return true;
		}

		/// <summary>
		/// Copies a range of bytes into a new array, zero-filling the part that falls outside <paramref name="data"/>.
		/// </summary>
		/// <param name="data">The source array.</param>
		/// <param name="offset">Offset of the first byte.</param>
		/// <param name="length">Length of the new array.</param>
		/// <returns>A new array of <paramref name="length"/> bytes.</returns>
		private static byte[] Slice(byte[] data, int offset, int length)
		{
			var result = new byte[length];
			Buffer.BlockCopy(data, offset, result, 0, Math.Max(0, Math.Min(length, data.Length - offset)));
			return result;
		}

		/// <summary>
		/// Concatenates byte arrays.
		/// </summary>
		/// <param name="parts">The arrays to concatenate.</param>
		/// <returns>A new array with all the bytes in order.</returns>
		private static byte[] Concat(params byte[][] parts)
		{
			var length = 0;
			foreach (var part in parts)
				length += part.Length;

			var result = new byte[length];
			var offset = 0;
			foreach (var part in parts)
			{
				Buffer.BlockCopy(part, 0, result, offset, part.Length);
				offset += part.Length;
			}

			return result;
		}

		#endregion
	}
}
