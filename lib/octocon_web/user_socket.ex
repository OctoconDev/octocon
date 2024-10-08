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

  alias OctoconWeb.SystemJSON
  alias OctoconWeb.System.TagJSON

  @impl true
  def join("system:" <> system_id, %{"token" => token} = params, socket) do
    # Only allow 2 socket joins per second to avoid abuse (especially from misconfigured Phoenix clients)
    # TODO: Check token first?
    case Hammer.check_rate("socket:#{system_id}", :timer.seconds(1), 2) do
      {:allow, _count} ->
        case Octocon.Auth.Guardian.resource_from_token(token) do
          {:ok, claim_id, _claims} when claim_id == system_id ->
            Process.send_after(socket.transport_pid, :garbage_collect, :timer.seconds(1))

            # TODO: Remove option once stable in prod?
            use_bare = Map.has_key?(params, "bare")
            {:ok, generate_init_data(system_id, use_bare), socket}

          _ ->
            {:error, %{reason: "unauthorized"}}
        end

      _ ->
        {:error, %{reason: "rate_limited"}}
    end
  rescue
    _ -> {:error, %{reason: "internal_error"}}
  end

  defp generate_init_data(system_id, use_bare) do
    identity = {:system, system_id}

    system =
      Accounts.get_user!(identity)
      |> SystemJSON.data_me()

    alters =
      if(use_bare) do
        Alters.get_alters_by_id(identity, Alters.bare_fields())
        |> Enum.map(&AlterJSON.data_me/1)
      else
        Alters.get_alters_by_id(identity)
        |> Enum.map(&AlterJSON.data_me/1)
      end

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

  @impl true
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
  @impl true
  def handle_info({:plug_conn, :sent}, socket), do: {:noreply, socket}

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
