defmodule OctoconWeb.UserSocket do
  use Phoenix.Socket

  channel "system:*", OctoconWeb.UserChannel

  def connect(%{"token" => token}, socket, _connect_info) do
    case Octocon.Auth.Guardian.resource_from_token(token) do
      {:ok, system_id, _claims} ->
        {
          :ok,
          socket
          |> assign(:system_id, system_id)
          |> assign(:token, token)
        }

      {:error, _reason} ->
        :error

      _ ->
        :error
    end
  end

  def id(socket), do: "system_socket:#{socket.assigns.system_id}"
end

defmodule OctoconWeb.UserChannel do
  use Phoenix.Channel

  @base_version Version.parse("1.0.0")
  @batched_init_version Version.parse("2.0.0")

  require Logger

  alias Octocon.{
    Accounts,
    Alters,
    Fronts,
    Tags
  }

  alias OctoconWeb.System.{
    AlterJSON,
    FrontingJSON
  }

  alias OctoconWeb.System.TagJSON
  alias OctoconWeb.SystemJSON

  @impl Phoenix.Channel
  def join("system:" <> system_id, %{"token" => token} = params, socket) do
    # Only allow 2 socket joins per second to avoid abuse (especially from misconfigured Phoenix clients)

    case Octocon.Auth.Guardian.resource_from_token(token) do
      {:ok, claim_id, _claims} when claim_id == system_id ->
        case Hammer.check_rate("socket:#{system_id}", :timer.seconds(1), 2) do
          {:allow, _count} ->
            is_reconnect = Map.get(params, "isReconnect", false)

            protocol_version =
              case Map.get(params, "protocolVersion") do
                nil -> {:ok, @base_version}
                version_str -> Version.parse(version_str)
              end

            platform = Map.get(params, "platform", "unknown")
            force_batch = Map.get(params, "forceBatch", false)

            case protocol_version do
              {:ok, version} ->
                if is_reconnect do
                  {:ok, socket}
                else
                  init_data = generate_init_data(system_id)

                  # What the fuck
                  exceeds_ios_limit = :erlang.external_size(init_data) * 1.1 > 1_048_576

                  if force_batch ||
                       (platform == "ios" && exceeds_ios_limit && version >= @batched_init_version) do
                    send(self(), {:send_batched_init, init_data})

                    response = %{
                      "batched" => true,
                      "system" => init_data["system"],
                      "alters" => nil,
                      "fronts" => nil,
                      "tags" => nil
                    }

                    {:ok, response, socket}
                  else
                    Process.send_after(socket.transport_pid, :garbage_collect, :timer.seconds(1))
                    {:ok, init_data, socket}
                  end
                end

              _ ->
                {:error, %{reason: "unsupported_protocol_version"}}
            end

          {:deny, _limit} ->
            {:error, %{reason: "rate_limited"}}
        end

      _ ->
        {:error, %{reason: "unauthorized"}}
    end
  rescue
    e ->
      Logger.error("Error joining user socket:")
      Logger.error(Exception.format(:error, e, __STACKTRACE__))
      {:error, %{reason: "internal_error"}}
  end

  defp generate_init_data(system_id) do
    identity = {:system, system_id}

    system =
      Accounts.get_user!(identity)
      |> SystemJSON.data_me()

    alters =
      Alters.get_alters_by_id(
        identity,
        Alters.bare_fields() ++ [:untracked, :archived, :last_fronted]
      )
      |> Enum.map(&AlterJSON.data_me/1)

    fronts =
      Fronts.currently_fronting(identity)
      |> Enum.map(&FrontingJSON.data_me/1)

    tags =
      Tags.get_tags(identity)
      |> Enum.map(&TagJSON.data_me/1)

    %{
      "system" => system,
      "alters" => alters,
      "fronts" => fronts,
      "tags" => tags
    }
  end

  defp send_batched_init(socket, %{"alters" => alters, "tags" => tags, "fronts" => fronts}) do
    send_batched_data(
      "batched_init_alters",
      "alters",
      3_000,
      alters,
      socket
    )

    send_batched_data(
      "batched_init_tags",
      "tags",
      1_000,
      tags,
      socket
    )

    send_batched_data(
      "batched_init_fronts",
      "fronts",
      50,
      fronts,
      socket
    )

    Process.send_after(socket.transport_pid, :garbage_collect, :timer.seconds(1))

    push(socket, "batched_init_complete", %{})
  end

  defp send_batched_data(event_name, data_name, batch_size, data, socket) do
    batched_data = Enum.chunk_every(data, batch_size)
    total_batches = length(batched_data)

    Enum.with_index(batched_data)
    |> Enum.each(fn {data_batch, index} ->
      Process.sleep(50)

      push(
        socket,
        event_name,
        %{
          "batch_index" => index + 1,
          "total_batches" => total_batches
        }
        |> Map.put(data_name, data_batch)
      )
    end)
  end

  @impl Phoenix.Channel
  def handle_in(
        "endpoint",
        %{
          "method" => method,
          "path" => path,
          "body" => body
        },
        socket
      ) do
    response =
      create_mock_conn(method, path, body, socket)
      |> Plug.Conn.put_req_header("authorization", "Bearer #{socket.assigns.token}")
      |> Plug.Conn.put_req_header("content-type", "application/json")
      |> Plug.Conn.put_req_header("accept", "application/json")
      |> OctoconWeb.Endpoint.call(nil)
      |> encode_response()

    {:reply, {:ok, response}, socket}
  end

  @doc false
  @impl Phoenix.Channel
  def handle_info({:plug_conn, :sent}, socket), do: {:noreply, socket}

  @impl Phoenix.Channel
  def handle_info({:send_batched_init, init_data}, socket) do
    if init_data do
      send_batched_init(socket, init_data)
    end

    {:noreply, socket}
  end

  defp encode_response(conn) do
    %{
      "status" => conn.status,
      # "headers" => conn.resp_headers,
      "body" => conn.resp_body
    }
  end

  defp create_mock_conn(method, path, body, _socket) do
    OctoconWeb.DummyConnAdapter.conn(%Plug.Conn{}, method, path, body)
  end
end
