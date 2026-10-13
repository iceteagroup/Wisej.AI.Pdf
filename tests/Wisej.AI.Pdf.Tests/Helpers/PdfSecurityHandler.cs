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

// Test helper: the encrypting side of the standard security handler, revisions
// 3 (RC4-128) and 4 (AES-128), Algorithms 1, 2, 3 and 5 of ISO 32000-2, 7.6.4.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Wisej.AI.Pdf.Tests.Helpers
{
	/// <summary>
	/// Encrypts strings and streams for <see cref="PdfBuilder"/>.
	/// </summary>
	/// <remarks>
	/// The implementation is independent from the library's decryptor, so the tests check one
	/// against the other. AES initialization vectors are derived from the object number to
	/// keep the generated files deterministic.
	/// </remarks>
	internal sealed class PdfSecurityHandler
	{
		// the 32-byte padding string of Algorithm 2.
		private static readonly byte[] Padding =
		{
			0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
			0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
		};

		// key length in bytes: both supported revisions use 128-bit keys.
		private const int KeyLength = 16;

		// the encryption settings.
		private readonly PdfEncryption _settings;

		// the file encryption key computed by Algorithm 2.
		private readonly byte[] _key;

		// the /O entry: the owner password check value.
		private readonly byte[] _owner;

		// the /U entry: the user password check value.
		private readonly byte[] _user;

		/// <summary>
		/// Initializes the handler and computes the keys.
		/// </summary>
		/// <param name="settings">The encryption settings.</param>
		/// <param name="documentId">The first element of the trailer /ID.</param>
		public PdfSecurityHandler(PdfEncryption settings, byte[] documentId)
		{
			_settings = settings;

			var userPassword = Pad(settings.UserPassword);
			var ownerPassword = Pad(string.IsNullOrEmpty(settings.OwnerPassword) ? settings.UserPassword : settings.OwnerPassword);

			_owner = ComputeOwnerEntry(ownerPassword, userPassword);
			_key = ComputeFileKey(userPassword, _owner, settings.Permissions, documentId);
			_user = ComputeUserEntry(_key, documentId);
		}

		/// <summary>
		/// The entries of the /Encrypt dictionary, without the enclosing delimiters.
		/// </summary>
		public string DictionaryEntries
		{
			get
			{
				var common = $"/Filter /Standard /Length 128 /P {_settings.Permissions} /O {PdfText.Hex(_owner)} /U {PdfText.Hex(_user)}";
				if (_settings.Cipher == PdfCipher.Rc4)
					return "/V 2 /R 3 " + common;

				return "/V 4 /R 4 /CF << /StdCF << /CFM /AESV2 /AuthEvent /DocOpen /Length 16 >> >> /StmF /StdCF /StrF /StdCF " + common;
			}
		}

		/// <summary>
		/// Encrypts the data of a string or stream that belongs to an indirect object.
		/// </summary>
		/// <param name="number">The object number.</param>
		/// <param name="data">The clear data.</param>
		/// <returns>The encrypted data.</returns>
		public byte[] Encrypt(int number, byte[] data)
		{
			var aes = _settings.Cipher == PdfCipher.Aes128;
			var key = ObjectKey(number, aes);
			if (!aes)
				return Rc4(key, data);

			using (var cipher = Aes.Create())
			{
				cipher.Mode = CipherMode.CBC;
				cipher.Padding = PaddingMode.PKCS7;
				cipher.Key = key;

				// a deterministic IV: real writers use random bytes, which the format allows either way.
				var iv = new byte[16];
				for (int i = 0; i < iv.Length; i++)
					iv[i] = (byte)(number * 31 + i * 7);

				cipher.IV = iv;
				using (var encryptor = cipher.CreateEncryptor())
				using (var output = new MemoryStream())
				{
					output.Write(iv, 0, iv.Length);
					var encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);
					output.Write(encrypted, 0, encrypted.Length);
					return output.ToArray();
				}
			}
		}

		/// <summary>
		/// Encrypts all the strings of an object body, rewriting them as hexadecimal strings.
		/// </summary>
		/// <param name="number">The object number.</param>
		/// <param name="body">The object body in PDF syntax.</param>
		/// <returns>The body with encrypted strings.</returns>
		public string EncryptStrings(int number, string body)
		{
			var sb = new StringBuilder(body.Length + 32);
			for (int i = 0; i < body.Length;)
			{
				var c = body[i];
				if (c == '(')
				{
					var bytes = ReadLiteral(body, ref i);
					sb.Append(PdfText.Hex(Encrypt(number, bytes)));
				}
				else if (c == '<' && i + 1 < body.Length && body[i + 1] == '<')
				{
					sb.Append("<<");
					i += 2;
				}
				else if (c == '<')
				{
					var end = body.IndexOf('>', i);
					var bytes = Convert.FromHexString(body.Substring(i + 1, end - i - 1).Replace(" ", string.Empty));
					sb.Append(PdfText.Hex(Encrypt(number, bytes)));
					i = end + 1;
				}
				else
				{
					sb.Append(c);
					i++;
				}
			}

			return sb.ToString();
		}

		#region Implementation

		// reads a literal string starting at "position" (on the opening parenthesis) and returns its bytes.
		private static byte[] ReadLiteral(string body, ref int position)
		{
			var bytes = new MemoryStream();
			var depth = 0;
			for (; position < body.Length; position++)
			{
				var c = body[position];
				if (c == '\\')
				{
					var next = body[++position];
					if (next >= '0' && next <= '7')
					{
						// up to three octal digits.
						var value = 0;
						var digits = 0;
						while (digits < 3 && body[position] >= '0' && body[position] <= '7')
						{
							value = value * 8 + (body[position] - '0');
							position++;
							digits++;
						}

						position--;
						bytes.WriteByte((byte)value);
					}
					else
					{
						bytes.WriteByte((byte)(next == 'n' ? '\n' : next == 'r' ? '\r' : next == 't' ? '\t' : next));
					}

					continue;
				}

				if (c == '(' && depth++ == 0)
					continue;

				if (c == ')' && --depth == 0)
				{
					position++;
					break;
				}

				bytes.WriteByte((byte)c);
			}

			return bytes.ToArray();
		}

		// pads or truncates a password to 32 bytes with the padding string (Algorithm 2, step a).
		private static byte[] Pad(string password)
		{
			var bytes = Encoding.ASCII.GetBytes(password ?? string.Empty);
			var padded = new byte[32];
			var length = Math.Min(32, bytes.Length);
			Buffer.BlockCopy(bytes, 0, padded, 0, length);
			Buffer.BlockCopy(Padding, 0, padded, length, 32 - length);
			return padded;
		}

		// Algorithm 3: the /O entry, revision 3 and later.
		private static byte[] ComputeOwnerEntry(byte[] ownerPassword, byte[] userPassword)
		{
			var hash = MD5.HashData(ownerPassword);
			for (int i = 0; i < 50; i++)
				hash = MD5.HashData(hash);

			var key = new byte[KeyLength];
			Buffer.BlockCopy(hash, 0, key, 0, KeyLength);

			var value = Rc4(key, userPassword);
			for (int i = 1; i <= 19; i++)
				value = Rc4(Xor(key, i), value);

			return value;
		}

		// Algorithm 2: the file encryption key.
		private static byte[] ComputeFileKey(byte[] userPassword, byte[] owner, int permissions, byte[] documentId)
		{
			var input = new MemoryStream();
			input.Write(userPassword, 0, 32);
			input.Write(owner, 0, 32);
			input.Write(BitConverter.GetBytes(permissions), 0, 4);
			input.Write(documentId, 0, documentId.Length);

			var hash = MD5.HashData(input.ToArray());
			for (int i = 0; i < 50; i++)
				hash = MD5.HashData(hash.AsSpan(0, KeyLength));

			var key = new byte[KeyLength];
			Buffer.BlockCopy(hash, 0, key, 0, KeyLength);
			return key;
		}

		// Algorithm 5: the /U entry, revision 3 and later.
		private static byte[] ComputeUserEntry(byte[] key, byte[] documentId)
		{
			var input = new byte[32 + documentId.Length];
			Buffer.BlockCopy(Padding, 0, input, 0, 32);
			Buffer.BlockCopy(documentId, 0, input, 32, documentId.Length);

			var value = Rc4(key, MD5.HashData(input));
			for (int i = 1; i <= 19; i++)
				value = Rc4(Xor(key, i), value);

			// the last 16 bytes are arbitrary padding.
			var entry = new byte[32];
			Buffer.BlockCopy(value, 0, entry, 0, 16);
			return entry;
		}

		// Algorithm 1: the key of an object's strings and streams.
		private byte[] ObjectKey(int number, bool aes)
		{
			var input = new MemoryStream();
			input.Write(_key, 0, _key.Length);
			input.WriteByte((byte)number);
			input.WriteByte((byte)(number >> 8));
			input.WriteByte((byte)(number >> 16));
			input.WriteByte(0);
			input.WriteByte(0);
			if (aes)
				input.Write(new byte[] { 0x73, 0x41, 0x6C, 0x54 }, 0, 4);

			var hash = MD5.HashData(input.ToArray());
			var key = new byte[Math.Min(_key.Length + 5, 16)];
			Buffer.BlockCopy(hash, 0, key, 0, key.Length);
			return key;
		}

		// returns a copy of the key with each byte XORed with the value, for the RC4 iterations of Algorithms 3 and 5.
		private static byte[] Xor(byte[] key, int value)
		{
			var result = new byte[key.Length];
			for (int i = 0; i < key.Length; i++)
				result[i] = (byte)(key[i] ^ value);

			return result;
		}

		// encrypts or decrypts the data with RC4.
		private static byte[] Rc4(byte[] key, byte[] data)
		{
			var s = new byte[256];
			for (int i = 0; i < 256; i++)
				s[i] = (byte)i;

			for (int i = 0, j = 0; i < 256; i++)
			{
				j = (j + s[i] + key[i % key.Length]) & 0xFF;
				(s[i], s[j]) = (s[j], s[i]);
			}

			var result = new byte[data.Length];
			for (int n = 0, i = 0, j = 0; n < data.Length; n++)
			{
				i = (i + 1) & 0xFF;
				j = (j + s[i]) & 0xFF;
				(s[i], s[j]) = (s[j], s[i]);
				result[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
			}

			return result;
		}

		#endregion
	}
}
