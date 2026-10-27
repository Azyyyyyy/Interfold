#include <netdb.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

/* Compose HEALTHCHECK has no HTTP probe, and chiseled has no curl/shell.
 * Exit 0 only on HTTP/1.x 200. See ApiComposeServicePublisher. */

static int fail(const char *msg)
{
    fprintf(stderr, "%s\n", msg);
    return 1;
}

int main(int argc, char **argv)
{
    if (argc != 2)
        return fail("usage: http-ready-probe http://host:port/path");

    const char *url = argv[1];
    if (strncmp(url, "http://", 7) != 0)
        return fail("only http:// is supported");
    url += 7;

    char host[256];
    char path[512];
    int port = 80;

    const char *slash = strchr(url, '/');
    const char *colon = strchr(url, ':');
    if (colon && (!slash || colon < slash))
    {
        size_t n = (size_t)(colon - url);
        if (n == 0 || n >= sizeof host)
            return fail("bad host");
        memcpy(host, url, n);
        host[n] = '\0';
        port = atoi(colon + 1);
        if (port <= 0 || port > 65535)
            return fail("bad port");
        if (slash)
        {
            if (strlen(slash) >= sizeof path)
                return fail("path too long");
            memcpy(path, slash, strlen(slash) + 1);
        }
        else
        {
            memcpy(path, "/", 2);
        }
    }
    else
    {
        size_t n = slash ? (size_t)(slash - url) : strlen(url);
        if (n == 0 || n >= sizeof host)
            return fail("bad host");
        memcpy(host, url, n);
        host[n] = '\0';
        if (slash)
        {
            if (strlen(slash) >= sizeof path)
                return fail("path too long");
            memcpy(path, slash, strlen(slash) + 1);
        }
        else
        {
            memcpy(path, "/", 2);
        }
    }

    char portstr[8];
    snprintf(portstr, sizeof portstr, "%d", port);

    struct addrinfo hints;
    memset(&hints, 0, sizeof hints);
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;

    struct addrinfo *res = NULL;
    if (getaddrinfo(host, portstr, &hints, &res) != 0)
        return fail("resolve failed");

    int fd = -1;
    for (struct addrinfo *ai = res; ai; ai = ai->ai_next)
    {
        fd = (int)socket(ai->ai_family, ai->ai_socktype, ai->ai_protocol);
        if (fd < 0)
            continue;
        struct timeval tv = { .tv_sec = 4, .tv_usec = 0 };
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof tv);
        if (connect(fd, ai->ai_addr, ai->ai_addrlen) == 0)
            break;
        close(fd);
        fd = -1;
    }
    freeaddrinfo(res);
    if (fd < 0)
        return fail("connect failed");

    char req[768];
    int n = snprintf(
        req, sizeof req,
        "GET %s HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n",
        path, host);
    if (n < 0 || n >= (int)sizeof req)
    {
        close(fd);
        return fail("request too long");
    }
    if (write(fd, req, (size_t)n) != n)
    {
        close(fd);
        return fail("write failed");
    }

    char buf[128];
    ssize_t got = read(fd, buf, sizeof buf - 1);
    close(fd);
    if (got < 12)
        return fail("short response");
    buf[got] = '\0';

    if (strncmp(buf, "HTTP/1.", 7) != 0)
        return fail("not http");
    const char *sp = strchr(buf, ' ');
    if (!sp || strncmp(sp + 1, "200", 3) != 0)
        return 1;
    return 0;
}
