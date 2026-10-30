// Headless replacement for Tizen Studio's Samsung Certificate Manager (TV / "VD" platform).
// Reuses CSRGeneratorForTizen from the cert-add-on plugin and talks to the same endpoints:
// Samsung account sign-in -> author cert -> distributor cert bound to the TV DUID(s).
//
// Usage: java -cp <plugin classes + libs> SamsungCert.java <outDir> <authorName> <duid>[,<duid>...]
//        java ... SamsungCert.java --distributor <outDir> <duid>[,<duid>...]  (new distributor only, author kept)
//        java -cp <...> SamsungCert.java --repack <outDir>   (rebuild the .p12 files from .pri/.crt)

import com.sun.net.httpserver.HttpServer;
import org.json.simple.JSONObject;
import org.json.simple.parser.JSONParser;
import org.tizen.common.cert.CSRGenerator.CSRGeneratorForTizen;

import javax.crypto.Cipher;
import javax.crypto.EncryptedPrivateKeyInfo;
import javax.crypto.SecretKeyFactory;
import javax.crypto.spec.PBEKeySpec;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.*;
import java.security.cert.*;
import java.security.spec.PKCS8EncodedKeySpec;
import java.util.*;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

public class SamsungCert {
    static final String LOGIN_URL = "https://account.samsung.com/mobile/account/check.do?serviceID=v285zxnl3h"
            + "&actionID=StartOAuth2&accessToken=Y&redirect_uri=http://localhost:4794/signin/callback";
    static final String AUTHOR_URL = "https://svdca.samsungqbe.com/apis/v3/authors";
    static final String DISTRIBUTOR_URL = "https://svdca.samsungqbe.com/apis/v3/distributors";
    static final String BOUNDARY = "*****";

    public static void main(String[] args) throws Exception {
        // Tizen's signer uses an old BouncyCastle that cannot read Java 17's default PBES2/AES PKCS#12.
        System.setProperty("keystore.pkcs12.certProtectionAlgorithm", "PBEWithSHA1AndDESede");
        System.setProperty("keystore.pkcs12.keyProtectionAlgorithm", "PBEWithSHA1AndDESede");
        System.setProperty("keystore.pkcs12.macAlgorithm", "HmacPBESHA1");

        if (args.length == 2 && args[0].equals("--repack")) {
            Path out = Paths.get(args[1]).toAbsolutePath();
            String password = Files.readAllLines(out.resolve("passwords.txt")).get(0).replaceFirst("^author=", "");
            writeP12(out, "author", password, Files.readString(out.resolve("author.crt")), "vd_tizen_dev_author_ca.cer");
            writeP12(out, "distributor", password, Files.readString(out.resolve("distributor.crt")), "vd_tizen_dev_public2.crt");
            System.out.println("[ok] repacked author.p12 and distributor.p12");
            return;
        }
        if (args.length == 3 && args[0].equals("--distributor")) {
            Path out = Paths.get(args[1]).toAbsolutePath();
            String password = Files.readAllLines(out.resolve("passwords.txt")).get(0).replaceFirst("^author=", "");
            Map<String, String> account = signIn();
            System.out.println("[ok] signed in as " + account.get("inputEmailID"));
            distributor(out, password, args[2].split(","), account);
            return;
        }
        if (args.length != 3) {
            System.err.println("usage: SamsungCert <outDir> <authorName> <duid>[,<duid>...]");
            System.exit(2);
        }
        Path out = Paths.get(args[0]).toAbsolutePath();
        String authorName = args[1];
        String[] duids = args[2].split(",");
        Files.createDirectories(out);

        String password = randomPassword();
        Path pwFile = out.resolve("passwords.txt");
        Files.writeString(pwFile, "author=" + password + "\ndistributor=" + password + "\n");
        pwFile.toFile().setReadable(false, false);
        pwFile.toFile().setReadable(true, true);

        Map<String, String> account = signIn();
        String accessToken = account.get("access_token");
        String userId = account.get("userId");
        String email = account.get("inputEmailID");
        System.out.println("[ok] signed in as " + email);

        // Author
        CSRGeneratorForTizen.generateCSR(new String[]{authorName, "", "", "", "", ""}, "author", password, out.toString());
        String authorCrt = request(AUTHOR_URL, accessToken, userId, out.resolve("author.csr"), Map.of());
        Files.writeString(out.resolve("author.crt"), authorCrt);
        writeP12(out, "author", password, authorCrt, "vd_tizen_dev_author_ca.cer");
        System.out.println("[ok] author.p12");

        distributor(out, password, duids, account);
        System.out.println("[ok] passwords in " + pwFile);
    }

    // Distributor (bound to the TV DUIDs)
    static void distributor(Path out, String password, String[] duids, Map<String, String> account) throws Exception {
        CSRGeneratorForTizen.generateCSRDistribute(new String[]{"TizenSDK", "", "", "", "", ""}, "distributor", password,
                out.toString(), duids, account.get("inputEmailID"));
        String distCrt = request(DISTRIBUTOR_URL, account.get("access_token"), account.get("userId"),
                out.resolve("distributor.csr"), linked("privilege_level", "Public", "developer_type", "Individual"));
        Files.writeString(out.resolve("distributor.crt"), distCrt);
        writeP12(out, "distributor", password, distCrt, "vd_tizen_dev_public2.crt");
        System.out.println("[ok] distributor.p12 (DUID: " + String.join(", ", duids) + ")");
    }

    // Opens the Samsung sign-in page and waits for the OAuth callback on localhost:4794.
    static Map<String, String> signIn() throws Exception {
        CompletableFuture<String> code = new CompletableFuture<>();
        HttpServer server = HttpServer.create(new InetSocketAddress("127.0.0.1", 4794), 0);
        server.createContext("/signin/callback", ex -> {
            String body = new String(ex.getRequestBody().readAllBytes(), StandardCharsets.UTF_8);
            if (body.isEmpty() && ex.getRequestURI().getRawQuery() != null) body = ex.getRequestURI().getRawQuery();
            String value = null;
            for (String kv : body.split("&")) {
                int i = kv.indexOf('=');
                if (i > 0 && kv.substring(0, i).equals("code")) value = URLDecoder.decode(kv.substring(i + 1), StandardCharsets.UTF_8);
            }
            byte[] page = (value != null ? "Login OK - you can close this tab." : "Login failed").getBytes(StandardCharsets.UTF_8);
            ex.sendResponseHeaders(200, page.length);
            ex.getResponseBody().write(page);
            ex.close();
            if (value != null) code.complete(value);
        });
        server.start();
        System.out.println("[..] open this URL in your browser and sign in with your Samsung account:");
        System.out.println(LOGIN_URL);
        System.out.flush();
        String[][] openers = {{"xdg-open", LOGIN_URL}, {"open", LOGIN_URL}, {"rundll32", "url.dll,FileProtocolHandler", LOGIN_URL}};
        for (String[] opener : openers) {  // Linux, macOS, Windows
            try {
                new ProcessBuilder(opener).redirectErrorStream(true).redirectOutput(ProcessBuilder.Redirect.DISCARD).start();
                break;
            } catch (IOException ignored) {
            }
        }
        String json = code.get(10, TimeUnit.MINUTES);
        server.stop(0);

        JSONObject obj = (JSONObject) new JSONParser().parse(json);
        Map<String, String> result = new HashMap<>();
        for (Object k : obj.keySet()) result.put(k.toString(), String.valueOf(obj.get(k)));
        if (result.get("access_token") == null || result.get("userId") == null)
            throw new IllegalStateException("unexpected sign-in response: " + json);
        return result;
    }

    static String request(String url, String token, String userId, Path csr, Map<String, String> extra) throws Exception {
        Map<String, String> fields = new LinkedHashMap<>();
        fields.put("access_token", token);
        fields.put("user_id", userId);
        fields.put("platform", "VD");
        fields.putAll(extra);

        ByteArrayOutputStream body = new ByteArrayOutputStream();
        PrintStream w = new PrintStream(body, true, StandardCharsets.UTF_8);
        for (Map.Entry<String, String> f : fields.entrySet()) {
            w.print("--" + BOUNDARY + "\r\nContent-Type: text/plain; charset=utf-8\r\n");
            w.print("Content-Disposition: form-data; name=" + f.getKey() + "\r\n\r\n" + f.getValue() + "\r\n");
        }
        String name = csr.getFileName().toString();
        w.print("--" + BOUNDARY + "\r\nContent-Disposition: form-data; name=csr; filename=" + name + "\r\n");
        w.print("Content-Type: text/plain; charset=utf-8\r\n\r\n");
        w.write(Files.readAllBytes(csr));
        w.print("\r\n--" + BOUNDARY + "--\r\n");

        HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
        c.setRequestMethod("POST");
        c.setDoOutput(true);
        c.setRequestProperty("content-type", "multipart/form-data; boundary=\"" + BOUNDARY + "\"");
        c.getOutputStream().write(body.toByteArray());
        int status = c.getResponseCode();
        InputStream in = status < 400 ? c.getInputStream() : c.getErrorStream();
        String resp = in == null ? "" : new String(in.readAllBytes(), StandardCharsets.UTF_8);
        if (status != 200 || !resp.contains("BEGIN CERTIFICATE"))
            throw new IllegalStateException(url + " -> HTTP " + status + ": " + resp);
        return resp;
    }

    // Bundles the encrypted key written by CSRGeneratorForTizen with the issued cert and Samsung's CA.
    static void writeP12(Path out, String name, String password, String crtPem, String caResource) throws Exception {
        EncryptedPrivateKeyInfo epki = new EncryptedPrivateKeyInfo(Files.readAllBytes(out.resolve(name + ".pri")));
        SecretKeyFactory skf = SecretKeyFactory.getInstance("PBEWithSHA1AndDESede");
        Cipher cipher = Cipher.getInstance("PBEWithSHA1AndDESede");
        cipher.init(Cipher.DECRYPT_MODE, skf.generateSecret(new PBEKeySpec(password.toCharArray())), epki.getAlgParameters());
        PKCS8EncodedKeySpec spec = epki.getKeySpec(cipher);
        PrivateKey key = KeyFactory.getInstance("RSA").generatePrivate(spec);

        CertificateFactory cf = CertificateFactory.getInstance("X.509");
        java.security.cert.Certificate leaf = cf.generateCertificate(new ByteArrayInputStream(crtPem.getBytes(StandardCharsets.US_ASCII)));
        java.security.cert.Certificate ca;
        try (InputStream caIn = SamsungCert.class.getClassLoader().getResourceAsStream("res/ca/" + caResource)) {
            ca = cf.generateCertificate(caIn);
        }

        KeyStore ks = KeyStore.getInstance("PKCS12");
        ks.load(null, null);
        ks.setKeyEntry("usercertificate", key, password.toCharArray(), new java.security.cert.Certificate[]{leaf, ca});
        try (OutputStream o = Files.newOutputStream(out.resolve(name + ".p12"))) {
            ks.store(o, password.toCharArray());
        }
    }

    static Map<String, String> linked(String... kv) {
        Map<String, String> m = new LinkedHashMap<>();
        for (int i = 0; i < kv.length; i += 2) m.put(kv[i], kv[i + 1]);
        return m;
    }

    static String randomPassword() {
        String alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        SecureRandom r = new SecureRandom();
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 20; i++) sb.append(alphabet.charAt(r.nextInt(alphabet.length())));
        return sb.toString();
    }
}
